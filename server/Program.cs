// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Kig.InvoiceBridge;

const int maxUpload = 1024 * 1024;
const int maxResult = 4 * 1024 * 1024;
Profile[] profiles;
ToolPaths tools;
try
{
    profiles = Profiles.Load(Environment.GetEnvironmentVariable("INVOICEBRIDGE_PROFILES") ?? "");
    tools = ToolPaths.Resolve(new BridgeConfiguration {
        Bank = profiles[0].Bank, SourceDirectory = ".", OutputDirectory = ".", SourceFileSelection = "InvoiceNumberToken",
        ToolsDirectory = Environment.GetEnvironmentVariable("INVOICEBRIDGE_TOOLS") ?? "/tools",
        GhostscriptExecutable = Environment.GetEnvironmentVariable("INVOICEBRIDGE_GHOSTSCRIPT")
    });
}
catch (Exception)
{
    Console.Error.WriteLine("InvoiceBridge: Profile oder Werkzeuge konnten nicht geladen werden.");
    return 2;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = maxUpload;
    // Only the container launcher sets this. The host-side published port is loopback-only.
    var address = Environment.GetEnvironmentVariable("INVOICEBRIDGE_CONTAINER") == "1" ? IPAddress.Any : IPAddress.Loopback;
    options.Listen(address, 8080, listen => listen.Protocols = HttpProtocols.Http1);
});
using var active = new SemaphoreSlim(2);
await using var app = builder.Build();
app.MapGet("/healthz", () => Results.NoContent());
app.MapPost("/v1/convert", async (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var keys = context.Request.Headers["X-Api-Key"];
    var profile = Profiles.Authenticate(profiles, keys.Count == 1 ? keys[0] : null);
    if (profile == null)
    {
        context.Response.Headers.WWWAuthenticate = "ApiKey";
        return Error(401, "api_key_required", "API-Key fehlt oder ist ungültig.");
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
            var sampleMatches = Kig.InvoiceBridge.Program.Hash(input) == Kig.InvoiceBridge.Program.SampleHash;
            try
            {
                if (ReadablePayment.Conflicts(input, profile.Bank))
                    return Error(422, "payment_conflict", "Lesbare Zahlungsdaten widersprechen dem Mandantenprofil.");
            }
            catch (Exception) when (!sampleMatches)
            {
                return Error(422, "unsupported_pdf", "Das Rechnungs-PDF konnte nicht als unterstütztes Muster erkannt werden.");
            }
            if (!sampleMatches)
                return Error(422, "unsupported_pdf", "Unterstützt wird nur die unveränderte Musterrechnung.");
            var candidate = await Kig.InvoiceBridge.Program.ConvertAsync(input, work.FullName, profile.Bank, tools,
                "112684", testPayment: true, cancellationToken: token);
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
