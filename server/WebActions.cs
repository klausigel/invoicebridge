// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Kig.InvoiceBridge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Core = Kig.InvoiceBridge.Program;

internal static class WebActions
{
    static readonly XNamespace R = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    static readonly XNamespace Cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    internal sealed record PaymentForm(string? Beneficiary, string? Iban, string? Bic, string? Amount, string? Reference);
    sealed record Check(string Name, string State);

    internal static void Map(WebApplication app, BrowserSessions sessions, ToolPaths tools, SemaphoreSlim active, int maxUpload)
    {
        var api = app.MapGroup("/v1");
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var profile = sessions.Authorize(http, out var denied);
            if (profile == null) return denied;
            http.Items[typeof(Profile)] = profile;
            return await next(context);
        });

        api.MapPost("/validate", (Func<HttpContext, Task<IResult>>)(ctx => WithUpload(ctx, active, maxUpload, false,
            (file, media, work, token) => Validate(file, work, tools, token))));
        api.MapPost("/payment/read", (Func<HttpContext, Task<IResult>>)(ctx => WithUpload(ctx, active, maxUpload, true,
            (file, media, work, token) => Task.FromResult(ReadPayment(file, media, (Profile)ctx.Items[typeof(Profile)]!)))));
        api.MapPost("/payment/qr", async (HttpContext ctx) =>
        {
            if (ctx.Request.ContentLength > 4096)
                return Error(413, "payment_too_large", "Die Zahlungsdaten sind zu lang.");
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 4096;
            try
            {
                var form = await ctx.Request.ReadFromJsonAsync<PaymentForm>(ctx.RequestAborted);
                if (form == null || form.Amount == null ||
                    !decimal.TryParse(form.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                    return Error(422, "payment_invalid", "Bitte einen gültigen Betrag mit höchstens zwei Nachkommastellen eingeben.");
                var bank = new PaymentDetails(Compact(form.Iban), form.Beneficiary?.Trim() ?? "",
                    Compact(form.Bic), "");
                var reference = form.Reference?.Trim() ?? "";
                var payload = bank.Payload(amount, reference, optionalBic: true);
                var matrix = PaymentDetails.EncodeQr(payload);
                var pixels = new byte[matrix.Width * matrix.Height * 3];
                for (int y = 0; y < matrix.Height; y++)
                    for (int x = 0; x < matrix.Width; x++)
                        pixels.AsSpan((y * matrix.Width + x) * 3, 3).Fill(matrix[x, y] ? (byte)0 : (byte)255);
                return Results.Json(new {
                    payment = new PaymentForm(bank.Beneficiary, bank.Iban, bank.Bic, amount.ToString("0.00", CultureInfo.InvariantCulture), reference),
                    png = Convert.ToBase64String(new RgbRaster(matrix.Width, matrix.Height, pixels).ToPng())
                });
            }
            catch (InvalidDataException ex) { return Error(422, "payment_invalid", ex.Message); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
            { return Error(413, "payment_too_large", "Die Zahlungsdaten sind zu lang."); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            { return Error(400, "invalid_payment_request", "Die Zahlungsdaten konnten nicht gelesen werden."); }
        });
    }

    static async Task<IResult> WithUpload(HttpContext ctx, SemaphoreSlim active, int limit, bool allowXml,
        Func<string, string, string, CancellationToken, Task<IResult>> action)
    {
        var media = ctx.Request.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        if (media != "application/pdf" && !(allowXml && media is "application/xml" or "text/xml"))
            return Error(415, "unsupported_media", allowXml ? "Erwartet wird PDF oder XML." : "Erwartet wird eine PDF.");
        if (ctx.Request.ContentLength > limit) return Error(413, "upload_too_large", "Die Datei überschreitet 1 MiB.");
        if (!active.Wait(0)) return Error(429, "busy", "Bitte später erneut versuchen.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var work = Directory.CreateTempSubdirectory("invoicebridge-http-");
            try
            {
                var input = Path.Combine(work.FullName, media == "application/pdf" ? "input.pdf" : "input.xml");
                await using (var stream = File.Create(input)) await ctx.Request.Body.CopyToAsync(stream, deadline.Token);
                return await action(input, media, work.FullName, deadline.Token);
            }
            finally { work.Delete(recursive: true); }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        { return Error(413, "upload_too_large", "Die Datei überschreitet 1 MiB."); }
        catch (BadHttpRequestException)
        { return Error(400, "invalid_upload", "Die Datei wurde nicht vollständig empfangen."); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Results.Empty; }
        catch (OperationCanceledException) { return Error(504, "timeout", "Das Zeitlimit wurde überschritten."); }
        catch (Exception) { return Error(503, "processing_failed", "Die Verarbeitung konnte technisch nicht abgeschlossen werden."); }
        finally { active.Release(); }
    }

    static async Task<IResult> Validate(string input, string work, ToolPaths tools, CancellationToken token)
    {
        Check[] checks = [
            new("PDF-Datei", "pending"), new("ZUGFeRD-Hybridstruktur (Mustang)", "pending"),
            new("PDF/A-3b (veraPDF)", "pending"), new("XML-Schema (CII EN 16931)", "pending"),
            new("XML-Geschäftsregeln (Mustang)", "pending")
        ];
        if (!TryReadPdf(input, out var attached))
        {
            checks[0] = checks[0] with { State = "invalid" };
            return Result("invalid");
        }
        checks[0] = checks[0] with { State = "passed" };
        await CheckTool(1, () => Core.CheckMustangAsync(tools.Java, tools.Mustang, input, Path.Combine(work, "hybrid"), token));
        await CheckTool(2, async () => (await Core.CheckPdfAAsync(tools, input, work, token)).Valid);
        if (attached is { Length: 1 } && attached[0].Memory.Length <= 1024 * 1024)
        {
            var xml = Path.Combine(work, "invoice.xml");
            await File.WriteAllBytesAsync(xml, attached[0].Memory, token);
            await CheckTool(3, () => Task.FromResult(Core.CheckSchema(tools.Mustang, xml)));
            await CheckTool(4, () => Core.CheckMustangAsync(tools.Java, tools.Mustang, xml, Path.Combine(work, "xml"), token));
        }
        else if (checks[1].State == "passed")
        {
            // A validated hybrid without one readable invoice attachment cannot be confirmed.
            checks[3] = checks[3] with { State = "error" };
        }
        return Result(checks.Any(c => c.State == "error") ? "error" : checks.All(c => c.State == "passed") ? "passed" : "invalid");

        IResult Result(string status) => Results.Json(new { status, checks }, statusCode: status == "error" ? 503 : 200);
        async Task CheckTool(int index, Func<Task<bool>> verify)
        {
            token.ThrowIfCancellationRequested();
            try { checks[index] = checks[index] with { State = await verify() ? "passed" : "invalid" }; }
            catch (Exception ex) when (ex is not OperationCanceledException) { checks[index] = checks[index] with { State = "error" }; }
        }
    }

    static bool TryReadPdf(string file, out EmbeddedFile[]? attachments)
    {
        attachments = null;
        try
        {
            using var pdf = PdfDocument.Open(file);
            if (pdf.NumberOfPages < 1) return false;
            if (pdf.Advanced.TryGetEmbeddedFiles(out var embedded))
                attachments = embedded.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            return true;
        }
        catch (Exception) { return false; }
    }

    static IResult ReadPayment(string file, string media, Profile profile)
    {
        try
        {
            if (media != "application/pdf")
            {
                using var input = File.OpenRead(file);
                return Prefill(ReadXml(input), "xml");
            }
            if (!TryReadPdf(file, out var attachments)) throw new InvalidDataException();
            if (attachments is { Length: > 1 }) throw new PaymentAmbiguous();
            if (attachments is { Length: 1 })
            {
                if (attachments[0].Memory.Length > 1024 * 1024) throw new InvalidDataException();
                using var input = new MemoryStream(attachments[0].Memory.ToArray());
                return Prefill(ReadXml(input), "xml");
            }
            var info = Core.Extract(file, profile.Bank);
            var decision = ReadablePayment.Decide(file, profile);
            if (decision.Error is "payment_ambiguous" or "payment_conflict" ||
                decision.Error == "payment_unclear" && ReadablePayment.HasBankText(file))
                throw new PaymentAmbiguous();
            var payment = decision.ReplaceGraphicBank ? null : decision.Payment;
            return Prefill(new PaymentForm(payment?.Beneficiary, payment?.Iban, payment?.Bic,
                info.Gross.ToString("0.00", CultureInfo.InvariantCulture), info.Number), "pdf");
        }
        catch (PaymentAmbiguous)
        { return Error(422, "payment_ambiguous", "Die Zahlungsangaben sind widersprüchlich oder nicht eindeutig. Es wurden keine Felder übernommen."); }
        catch (Exception)
        { return Error(422, "unsupported_invoice", "Die Rechnung konnte nicht als unterstützte Vorlage oder ZUGFeRD-XML gelesen werden."); }
    }

    static IResult Prefill(PaymentForm fields, string source) => Results.Json(new { fields, source });

    static PaymentForm ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name != Cii + "CrossIndustryInvoice") throw new InvalidDataException();
        var settlement = One(doc.Descendants(R + "ApplicableHeaderTradeSettlement")) ?? throw new InvalidDataException();
        var currency = Text(settlement, "InvoiceCurrencyCode");
        if (currency is not null and not "EUR") throw new InvalidDataException();
        var means = One(settlement.Elements(R + "SpecifiedTradeSettlementPaymentMeans"));
        var type = Text(means, "TypeCode");
        if (type is not null and not ("30" or "58")) throw new InvalidDataException();
        var account = One(means?.Elements(R + "PayeePartyCreditorFinancialAccount") ?? []);
        var institution = One(means?.Elements(R + "PayeeSpecifiedCreditorFinancialInstitution") ?? []);
        var payee = Text(One(settlement.Elements(R + "PayeeTradeParty")), "Name");
        var accountName = Text(account, "AccountName");
        if (payee != null && accountName != null && !string.Equals(payee, accountName, StringComparison.OrdinalIgnoreCase))
            throw new PaymentAmbiguous();
        var totals = One(settlement.Elements(R + "SpecifiedTradeSettlementHeaderMonetarySummation"));
        var amount = One(totals?.Elements(R + "DuePayableAmount") ?? []);
        var amountCurrency = (string?)amount?.Attribute("currencyID");
        if (amountCurrency is not null and not "EUR") throw new InvalidDataException();
        return new PaymentForm(accountName ?? payee, Text(account, "IBANID"), Text(institution, "BICID"),
            currency == "EUR" || amountCurrency == "EUR" ? amount?.Value.Trim() : null, Text(settlement, "PaymentReference"));
    }

    static XElement? One(IEnumerable<XElement> elements)
    {
        var candidates = elements.Take(2).ToArray();
        if (candidates.Length > 1) throw new PaymentAmbiguous();
        return candidates.FirstOrDefault();
    }
    static string? Text(XElement? parent, string field) => One(parent?.Elements(R + field) ?? [])?.Value.Trim();
    static string Compact(string? value) => string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    sealed class PaymentAmbiguous : Exception;
    static IResult Error(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
