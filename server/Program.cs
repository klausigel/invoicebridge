// SPDX-License-Identifier: AGPL-3.0-or-later
using Kig.InvoiceBridge;

const int maxUpload = 1024 * 1024;
const int maxResult = 4 * 1024 * 1024;
Profile[] profiles;
ToolPaths tools;
BrowserSessions sessions;
LocalHosting hosting;
ProfileMode mode;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args, EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
try
{
    mode = Profiles.LocalMode();
    hosting = LocalHosting.Resolve(builder.Configuration, mode);
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine("InvoiceBridge: " + ex.Message);
    return 2;
}
try
{
    profiles = Profiles.Load(Environment.GetEnvironmentVariable("INVOICEBRIDGE_PROFILES") ?? "", mode);
    sessions = new BrowserSessions(profiles, BrowserSessions.LocalLifetime(), TimeProvider.System);
    tools = ToolPaths.Resolve(new BridgeConfiguration {
        Bank = profiles[0].Bank, SourceDirectory = ".", OutputDirectory = ".", SourceFileSelection = "InvoiceNumberToken",
        ToolsDirectory = Environment.GetEnvironmentVariable("INVOICEBRIDGE_TOOLS") ?? "/tools",
        GhostscriptExecutable = Environment.GetEnvironmentVariable("INVOICEBRIDGE_GHOSTSCRIPT")
    });
}
catch (Exception)
{
    Console.Error.WriteLine("InvoiceBridge: Profile oder Werkzeuge konnten nicht geladen werden. Profilmodus, Dateipfad, zwei unterschiedliche Schlüssel und Bankfelder prüfen.");
    return 2;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = maxUpload;
    hosting.Configure(options);
});
using var active = new SemaphoreSlim(2);
await using var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.NoContent());
sessions.Map(app);
WebActions.Map(app, sessions, tools, active, maxUpload);
app.MapPost("/v1/convert", async (HttpContext context) =>
{
    var profile = sessions.Authorize(context, out var denied);
    if (profile == null) return denied;
    bool includeGiroCode = true;
    if (context.Request.Query.TryGetValue("giroCode", out var values))
    {
        if (values.Count != 1 || values[0] is not ("true" or "false"))
            return Error(400, "invalid_giro_code_option", "giroCode muss genau einmal den Wert true oder false haben.");
        includeGiroCode = values[0] == "true";
    }
    if (!string.Equals(context.Request.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        return Error(415, "pdf_required", "Erwartet wird application/pdf als Request-Body.");
    if (context.Request.ContentLength > maxUpload)
        return Error(413, "upload_too_large", "Die PDF überschreitet 1 MiB.");
    if (!active.Wait(0)) return Error(429, "busy", "Bitte später erneut versuchen.");

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(120));
    var token = deadline.Token;
    try
    {
        var work = Directory.CreateTempSubdirectory("invoicebridge-http-");
        try
        {
            var input = Path.Combine(work.FullName, "input.pdf");
            await using (var upload = File.Create(input))
                await context.Request.Body.CopyToAsync(upload, token);
            var kig = KigInvoice.Read(input, profile.Bank, profile.Id == "KIG" && profile.Mode == ProfileMode.LocalReal);
            if (kig == null && profile.Mode == ProfileMode.LocalReal)
            {
                try { _ = Kig.InvoiceBridge.Program.Extract(input, profile.Bank); }
                catch (BridgeException) { throw; }
                catch (Exception)
                {
                    return Error(422, "unsupported_pdf", "Die benötigten Felder der bekannten Rechnungsvorlage konnten nicht eindeutig gelesen werden.");
                }
            }
            PaymentDecision decision;
            try
            {
                decision = kig == null
                    ? ReadablePayment.Decide(input, profile)
                    : new PaymentDecision(kig.Payment, false, null);
            }
            catch (Exception)
            {
                return Error(422, "unsupported_pdf", "Das Rechnungs-PDF konnte nicht als unterstütztes Muster erkannt werden.");
            }
            if (decision.Payment == null)
                return decision.Error switch {
                    "payment_conflict" => Error(422, "payment_conflict", "Lesbare Zahlungsdaten widersprechen dem Mandantenprofil."),
                    "payment_ambiguous" => Error(422, "payment_ambiguous", "Mehr als eine plausible Bankverbindung oder Zahlungsangabe."),
                    _ => Error(422, "payment_unclear", "Der Bankblock fehlt, ist unklar oder ist für einen Ersatz nicht freigegeben.")
                };
            var candidate = await Kig.InvoiceBridge.Program.ConvertAsync(input, work.FullName, decision.Payment, tools,
                kig?.Number ?? "112684", replaceGraphicBank: decision.ReplaceGraphicBank, cancellationToken: token,
                requireBlankQrArea: true, includeGiroCode: includeGiroCode, extractedInvoice: kig);
            if (new FileInfo(candidate).Length > maxResult)
                throw new InvalidDataException();
            var result = await File.ReadAllBytesAsync(candidate, token);
            return Results.Bytes(result, "application/pdf");
        }
        finally { work.Delete(recursive: true); }
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413)
    {
        return Error(413, "upload_too_large", "Die PDF überschreitet 1 MiB.");
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
    {
        return Error(400, "invalid_upload", "Der Upload wurde nicht vollständig empfangen.");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.Empty;
    }
    catch (OperationCanceledException)
    {
        return Error(504, "conversion_timeout", "Zeitlimit für die Verarbeitung überschritten.");
    }
    catch (Kig.InvoiceBridge.Program.ToolFailure)
    {
        return Error(500, "tool_failed", "Ein Konvertierungs- oder Prüfwerkzeug ist fehlgeschlagen.");
    }
    catch (BridgeException ex) when (ex.ExitCode is 14 or 15)
    {
        return ex.ExitCode == 14
            ? Error(403, "tenant_template_mismatch", ex.Message)
            : Error(422, "payment_conflict", ex.Message);
    }
    catch (BridgeException ex) when (ex.ExitCode == 11)
    {
        return Error(422, "unsupported_pdf", ex.Message);
    }
    catch (Exception)
    {
        return Error(500, "conversion_failed", "Verarbeitung oder Validierung ist fehlgeschlagen.");
    }
    finally { active.Release(); }
});
await app.RunAsync();
return 0;

static IResult Error(int status, string code, string title) =>
    Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
