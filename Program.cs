// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;
using UglyToad.PdfPig;
using s2industries.ZUGFeRD;

namespace Kig.InvoiceBridge;

internal static class Program
{
    const string SampleHash = "B5F46736EFB3DF675BA7F4E8BFB8B3EC2E73732CB71FA186AD4777F91F2F300C";
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--decode-qr-gray")
        {
            try { return PaymentDetails.DecodeGray(args[1], int.Parse(args[2]), int.Parse(args[3])); }
            catch (Exception ex) { Console.Error.WriteLine("FEHLER: " + ex.Message); return 1; }
        }
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("invoicebridge <Rechnungsnummer> [--config <JSON-Datei>]\nNur die freigegebene Musterrechnung 112684 wird unterstützt.\nStandardkonfiguration: invoicebridge.json neben der Anwendung. Relative Datenpfade gelten ab Konfigurationsordner.\nExitcodes: 0 Erfolg, 2 Aufruf/Konfiguration, 10 Rechnung fehlt, 11 Muster nicht unterstützt, 12 Ausgabe existiert, 13 mehrere Quelldateien, 20 Abhängigkeit fehlt, 21 Prüfung gescheitert, 30 Ein-/Ausgabefehler.");
            return 0;
        }
        string? evidence = null, pending = null;
        int failureExitCode = 2;
        try
        {
            string? invoiceNumber = null, configArgument = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config")
                {
                    if (configArgument != null || ++i >= args.Length || args[i].StartsWith('-')) throw new BridgeException(2, "--config erwartet genau einen Dateipfad.");
                    configArgument = args[i];
                }
                else if (invoiceNumber == null && Regex.IsMatch(args[i], "^[0-9]{1,20}$")) invoiceNumber = args[i];
                else throw new BridgeException(2, "Aufruf: invoicebridge <Rechnungsnummer> [--config <JSON-Datei>]");
            }
            if (invoiceNumber == null) throw new BridgeException(2, "Rechnungsnummer fehlt. Aufruf: invoicebridge 112684");
            var (settings, _) = BridgeConfiguration.Load(configArgument);
            var input = InvoiceSourceResolver.Resolve(settings.SourceDirectory, invoiceNumber);
            var output = Path.Combine(settings.OutputDirectory, "Rechnung_" + invoiceNumber + "_ZUGFeRD.pdf");
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new BridgeException(2, "Quell- und Zieldatei dürfen nicht gleich sein.");
            if (File.Exists(output)) throw new BridgeException(12, "Ausgabedatei existiert bereits: " + output);
            if (Hash(input) != SampleHash) throw new BridgeException(11, "Nicht unterstütztes PDF: Nur die unveränderte freigegebene Musterrechnung 112684 ist erlaubt.");
            var tools = ToolPaths.Resolve(settings);
            var mustang = tools.Mustang;
            var vera = tools.VeraPdf;
            Directory.CreateDirectory(settings.OutputDirectory);
            FileStream reservation;
            try { reservation = new FileStream(output + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { throw new BridgeException(12, "Ausgabe ist durch einen anderen Lauf oder eine Sperrdatei belegt: " + output + ".lock"); }
            using var outputReservation = reservation;
            var work = Path.Combine(settings.OutputDirectory, ".invoicebridge-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(work);
            else Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            evidence = work;
            var timer = Stopwatch.StartNew();
            failureExitCode = 11;
            var snapshot = Extract(input, settings.Bank);
            if (snapshot.Number != invoiceNumber) throw new BridgeException(11, "Die Rechnungsnummer im PDF stimmt nicht mit der angeforderten Nummer überein.");
            failureExitCode = 21;
            var xml = Path.Combine(evidence, "factur-x.xml");
            WriteXml(snapshot, xml);
            ValidateSchema(mustang, xml);
            Console.WriteLine("PASS xsd");

            // XML must pass before the hybrid PDF is created. No network or web validators.
            await ValidateMustang(tools.Java, mustang, xml, Path.Combine(evidence, "xml-validation"));
            var withGiroCode = Path.Combine(evidence, "with-girocode.pdf");
            snapshot.Payment.AddGiroCode(input, withGiroCode, snapshot.Gross, snapshot.Number);
            var basePdf = Path.Combine(evidence, "pdfa3-base.pdf");
            var definition = Path.Combine(evidence, "pdfa.ps");
            var icc = tools.IccProfile.Replace('\\', '/');
            File.WriteAllText(definition, "%!\n/ICCProfile (" + icc.Replace("(", "\\(").Replace(")", "\\)") + ") def\n[/_objdef {icc_PDFA} /type /stream /OBJ pdfmark\n[{icc_PDFA} << /N 3 >> /PUT pdfmark\n[{icc_PDFA} ICCProfile (r) file /PUT pdfmark\n[/_objdef {OutputIntent_PDFA} /type /dict /OBJ pdfmark\n[{OutputIntent_PDFA} << /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile {icc_PDFA} /OutputConditionIdentifier (sRGB) >> /PUT pdfmark\n[{Catalog} << /OutputIntents [ {OutputIntent_PDFA} ] >> /PUT pdfmark\n");
            await Run(tools.Ghostscript, ["-dSAFER", "-dBATCH", "-dNOPAUSE", "-sDEVICE=pdfwrite", "-dPDFA=3", "-dPDFACompatibilityPolicy=2", "-sColorConversionStrategy=RGB", "-dAutoRotatePages=/None", "-dDownsampleColorImages=false", "-dDownsampleGrayImages=false", "-dDownsampleMonoImages=false", "--permit-file-read=" + icc, "-sOutputFile=" + basePdf, definition, withGiroCode], Path.Combine(evidence, "ghostscript"));
            var candidate = Path.Combine(evidence, "candidate.pdf");
            await Run(tools.Java, ["-jar", mustang, "--action", "combine", "--source", basePdf, "--source-xml", xml, "--out", candidate, "--format", "fx", "--version", "2", "--profile", "E", "--no-additional-attachments", "--disable-file-logging"], Path.Combine(evidence, "combine"));
            await ValidateMustang(tools.Java, mustang, candidate, Path.Combine(evidence, "hybrid-validation"));
            // Same veraPDF CLI wrapper as verapdf.bat, invoked directly to use
            // the bundled JRE safely with paths containing spaces. Separate check retained.
            var veraResult = await Run(tools.Java, ["-Dfile.encoding=UTF8", "-XX:+IgnoreUnrecognizedVMOptions", "-classpath", Path.Combine(Path.GetDirectoryName(vera)!, "*"), "--add-exports=java.base/sun.security.pkcs=ALL-UNNAMED", "org.verapdf.apps.GreenfieldCliWrapper", "--format", "xml", "--flavour", "3b", candidate], Path.Combine(evidence, "pdfa-validation"));
            var veraXml = ParseReport(veraResult);
            var reports = veraXml.DescendantsAndSelf().Where(x => x.Name.LocalName == "validationReport").ToArray();
            if (reports.Length != 1 || (string?)reports[0].Attribute("isCompliant") != "true") throw new InvalidDataException("Finale PDF/A-3b-Prüfung fehlgeschlagen. Siehe Prüfprotokoll.");
            var counts = reports[0].Elements().Single(e => e.Name.LocalName == "details");
            Console.WriteLine($"PASS pdfa-3b rules={(int?)counts.Attribute("passedRules")} checks={(int?)counts.Attribute("passedChecks")}");
            var extracted = Path.Combine(evidence, "extracted.xml");
            await Run(tools.Java, ["-jar", mustang, "--action", "extract", "--source", candidate, "--out", extracted, "--disable-file-logging"], Path.Combine(evidence, "extract"));
            if (!File.Exists(extracted) || !XNode.DeepEquals(XDocument.Load(xml), XDocument.Load(extracted))) throw new InvalidDataException("Rückextrahierte XML stimmt nicht mit der erzeugten XML überein.");
            CheckXml(snapshot, XDocument.Load(extracted));
            Console.WriteLine("PASS xml-roundtrip-and-fields");
            // Keep QR decoding and the visual comparison available in the deployed
            // CLI without relying on the development Python/Poppler installation.
            var originalRender = Path.Combine(evidence, "original-render.ppm");
            var finalRender = Path.Combine(evidence, "final-render.ppm");
            await Run(tools.Ghostscript, ["-dSAFER", "-dBATCH", "-dNOPAUSE", "-sDEVICE=ppmraw", "-r150", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-sOutputFile=" + originalRender, input], Path.Combine(evidence, "render-original"));
            await Run(tools.Ghostscript, ["-dSAFER", "-dBATCH", "-dNOPAUSE", "-sDEVICE=ppmraw", "-r150", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-sOutputFile=" + finalRender, candidate], Path.Combine(evidence, "render-final"));
            snapshot.Payment.VerifyRendered(originalRender, finalRender, snapshot.Gross, snapshot.Number);
            Console.WriteLine("PASS rendered-qr-and-image");
            if (Hash(input) != SampleHash) throw new InvalidDataException("Originaldatei wurde während des Laufs verändert.");
            pending = work + ".pending";
            File.Move(candidate, pending, false);
            Directory.Delete(work, recursive: true);
            evidence = null;
            try { File.Move(pending, output, false); }
            catch (IOException) when (File.Exists(output)) { throw new BridgeException(12, "Ausgabe wurde zwischenzeitlich angelegt und wird nicht überschrieben: " + output); }
            pending = null;
            timer.Stop();
            Console.WriteLine(FormattableString.Invariant($"OK conversion_seconds={timer.Elapsed.TotalSeconds:F3}"));
            return 0;
        }
        catch (Exception ex)
        {
            var code = ex is BridgeException bridge ? bridge.ExitCode : ex is IOException or UnauthorizedAccessException ? 30 : failureExitCode;
            var detail = ex is ToolFailure tool ? $"Werkzeugschritt {tool.Stage} fehlgeschlagen ({tool.ExitCode})."
                : ex is BridgeException && code is 20 or 21 ? ex.Message
                : code switch {
                    2 => "Aufruf oder Konfiguration ungültig. Siehe --help.",
                    10 => "Quelle oder passende PDF fehlt.",
                    11 => "Die PDF entspricht nicht dem unterstützten Muster.",
                    12 => "Ausgabe vorhanden oder bereits reserviert.",
                    13 => "Mehrere passende PDFs im Quellordner.",
                    21 => "Verarbeitung oder Prüfung fehlgeschlagen. Siehe letzten Prüfstatus.",
                    30 => "Dateizugriff oder Ausgabe fehlgeschlagen.",
                    _ => "Verarbeitung fehlgeschlagen."
                };
            Console.Error.WriteLine($"FEHLER {code}: {detail}");
            return code;
        }
        finally
        {
            try
            {
                if (pending != null) File.Delete(pending);
                if (evidence != null) Directory.Delete(evidence, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Arbeitsdaten konnten nicht vollständig entfernt werden.");
            }
        }
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    static Snapshot Extract(string path, PaymentDetails payment)
    {
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages != 1) throw new InvalidDataException("Eine Seite erwartet.");
        var page = pdf.GetPage(1);
        if (Math.Abs(page.Width - 595) > 1 || Math.Abs(page.Height - 842) > 1) throw new InvalidDataException("Unerwartetes Seitenformat.");
        var words = page.GetWords().ToArray();
        var fields = new Dictionary<string, Field>();
        string Region(string key, double left, double top, double right, double bottom)
        {
            var selected = words.Where(w => w.BoundingBox.Left >= left && w.BoundingBox.Right <= right && page.Height - w.BoundingBox.Top >= top && page.Height - w.BoundingBox.Bottom <= bottom).OrderByDescending(w => w.BoundingBox.Bottom).ToList();
            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            foreach (var w in selected)
            {
                var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 2);
                if (line == null) lines.Add([w]); else line.Add(w);
            }
            var text = string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Pflichtbereich nicht lesbar: " + key);
            fields.Add(key, new(text, "PDF text, page 1, region in points from top-left", [left, top, right, bottom]));
            return text;
        }
        string Match(string text, string pattern) { var m = Regex.Match(text, pattern); if (!m.Success) throw new InvalidDataException("Unerwarteter Inhalt: " + text); return m.Groups[1].Value; }
        decimal Amount(string text) => decimal.Parse(Match(text, @"^(\d+,\d{2})(?: EUR|%)?$"), German);
        var no = Region("invoiceNumber", 119, 359, 175, 374);
        var date = DateTime.ParseExact(Region("invoiceDate", 119, 373, 175, 388), "dd.MM.yyyy", German);
        var delivery = DateTime.ParseExact(Region("deliveryDate", 345, 356, 405, 372), "dd.MM.yyyy", German);
        var seller = Region("seller", 69, 178, 265, 189);
        var buyer = Region("buyer", 69, 200, 240, 213);
        var street = Region("buyerStreet", 69, 212, 240, 225);
        var city = Region("buyerCity", 69, 235, 245, 248);
        var vatId = Region("sellerVatId", 119, 689, 190, 704);
        var name = Region("itemName", 69, 424, 275, 446).Replace('\n', ' ');
        var description = Region("itemDescription", 69, 445, 275, 523).Replace('\n', ' ');
        var quantity = Amount(Region("quantity", 292, 423, 312, 437));
        var unit = Region("unit", 314, 423, 340, 437);
        var rate = Amount(Region("vatRate", 348, 423, 381, 437));
        var price = Amount(Region("unitPrice", 402, 423, 451, 437));
        var lineNet = Amount(Region("lineNet", 509, 423, 558, 437));
        var net = Amount(Region("netTotal", 509, 535, 558, 548));
        var tax = Amount(Region("vatTotal", 509, 549, 558, 563));
        var gross = Amount(Region("grossTotal", 509, 564, 558, 578));
        var terms = Region("paymentTerms", 113, 708, 161, 723);
        if (no != "112684" || unit != "Stck." || rate != 19 || terms != "Vorkasse" || !seller.StartsWith("NetProof GmbH ") || !name.StartsWith("HelloExchange 1.x HV")) throw new InvalidDataException("Der geprüfte Musterfall wurde nicht erkannt.");
        if (quantity * price != lineNet || lineNet != net || decimal.Round(net * rate / 100, 2, MidpointRounding.AwayFromZero) != tax || net + tax != gross || gross != 456.01m) throw new InvalidDataException("Betrags- oder Rundungsprüfung fehlgeschlagen.");
        fields["countryCodes"] = new("DE/DE", "Explicitly confirmed by Klaus; not printed as country codes", null);
        fields["taxCategory"] = new("S", "Explicitly confirmed regular VAT by Klaus", null);
        fields["prepaidAmount"] = new("0.00", "Explicitly confirmed invoice at issuance; no payment received", null);
        fields["unitCode"] = new("H87", "Documented normalization of Stck. to piece", null);
        fields["invoiceType"] = new("380", "Normal invoice, based on visible Rechnung label; fixed sample only", null);
        fields["lineId"] = new("1", "Generated sequential XML line identifier", null);
        payment.Validate("NetProof GmbH");
        fields["bankDetails"] = new(string.Join(" / ", payment.Iban, payment.Beneficiary, payment.Bic, payment.BankName), "Configuration: master data explicitly confirmed by Klaus on 2026-11-02; approved sample fingerprint checked", null);
        fields["paymentMeans"] = new("58", "SEPA credit transfer for requested GiroCode; Vorkasse remains unchanged", null);
        fields["paymentReference"] = new(no, "Existing invoice number reused as unstructured remittance; no RF creditor reference invented", null);
        return new(no, date, delivery, "NetProof GmbH", Match(seller, @"NetProof GmbH (.+) 21376 Salzhausen"), "21376", "Salzhausen", vatId, buyer, street, Match(city, @"^(\d{5}) "), Match(city, @"^\d{5} (.+)$"), name, description, quantity, price, lineNet, rate, net, tax, gross, terms, fields, payment);
    }

    static void WriteXml(Snapshot s, string path)
    {
        var invoice = InvoiceDescriptor.CreateInvoice(s.Number, s.Date, CurrencyCodes.EUR);
        invoice.Type = InvoiceType.Invoice;
        invoice.SetSeller(s.Seller, s.SellerPostcode, s.SellerCity, s.SellerStreet, CountryCodes.DE);
        invoice.AddSellerTaxRegistration(s.VatId, TaxRegistrationSchemeID.VA);
        invoice.SetBuyer(s.Buyer, s.BuyerPostcode, s.BuyerCity, s.BuyerStreet, CountryCodes.DE);
        invoice.ActualDeliveryDate = s.Delivery;
        invoice.AddTradePaymentTerms(s.Terms);
        s.Payment.Validate(s.Seller);
        invoice.SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer);
        invoice.PaymentReference = s.Number;
        invoice.AddCreditorFinancialAccount(s.Payment.Iban, s.Payment.Bic, bankName: s.Payment.BankName, name: s.Payment.Beneficiary);
        invoice.AddTradeLineItem(lineID: "1", name: s.ItemName, description: s.Description, netUnitPrice: s.Price, unitCode: QuantityCodes.H87, unitQuantity: 1, billedQuantity: s.Quantity, lineTotalAmount: s.LineNet, taxType: TaxTypes.VAT, categoryCode: TaxCategoryCodes.S, taxPercent: s.Rate);
        invoice.AddApplicableTradeTax(s.Net, s.Rate, s.Tax, TaxTypes.VAT, TaxCategoryCodes.S);
        invoice.SetTotals(lineTotalAmount: s.LineNet, chargeTotalAmount: 0, allowanceTotalAmount: 0, taxBasisAmount: s.Net, taxTotalAmount: s.Tax, grandTotalAmount: s.Gross, totalPrepaidAmount: 0, duePayableAmount: s.Gross, roundingAmount: 0);
        invoice.Save(path, ZUGFeRDVersion.Version23, Profile.Comfort);
    }

    static void CheckXml(Snapshot s, XDocument doc)
    {
        XNamespace r = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
        var ex = doc.Descendants().Single(x => x.Name.LocalName == "ExchangedDocument");
        if ((string?)ex.Element(r + "ID") != s.Number || (string?)ex.Element(r + "TypeCode") != "380") throw new InvalidDataException("XML-Kopf stimmt nicht überein.");
        var line = doc.Descendants(r + "IncludedSupplyChainTradeLineItem").Single();
        var product = line.Element(r + "SpecifiedTradeProduct")!;
        if ((string?)product.Element(r + "Name") != s.ItemName || (string?)product.Element(r + "Description") != s.Description) throw new InvalidDataException("XML-Positionsbeschreibung stimmt nicht überein.");
        void Check(string name, decimal expected) { var n = doc.Descendants(r + name).Last(); if (decimal.Parse(n.Value, CultureInfo.InvariantCulture) != expected) throw new InvalidDataException("XML-Betrag stimmt nicht überein: " + name); }
        Check("LineTotalAmount", s.LineNet); Check("TaxBasisTotalAmount", s.Net); Check("TaxTotalAmount", s.Tax); Check("GrandTotalAmount", s.Gross); Check("TotalPrepaidAmount", 0); Check("DuePayableAmount", s.Gross);
        if (doc.Descendants(r + "SellerTradeParty").Single().Element(r + "Name")?.Value != s.Seller || doc.Descendants(r + "BuyerTradeParty").Single().Element(r + "Name")?.Value != s.Buyer) throw new InvalidDataException("XML-Parteien stimmen nicht überein.");
        var payment = doc.Descendants(r + "SpecifiedTradeSettlementPaymentMeans").Single();
        if (payment.Element(r + "TypeCode")?.Value != "58" || payment.Element(r + "PayeePartyCreditorFinancialAccount")?.Element(r + "IBANID")?.Value != s.Payment.Iban || payment.Element(r + "PayeePartyCreditorFinancialAccount")?.Element(r + "AccountName")?.Value != s.Payment.Beneficiary || payment.Element(r + "PayeeSpecifiedCreditorFinancialInstitution")?.Element(r + "BICID")?.Value != s.Payment.Bic || doc.Descendants(r + "PaymentReference").Single().Value != s.Number) throw new InvalidDataException("XML-Zahlungsdaten stimmen nicht überein.");
    }

    static async Task ValidateMustang(string java, string jar, string source, string log)
    {
        var result = await Run(java, ["-jar", jar, "--action", "validate", "--source", source, "--no-notices", "--disable-file-logging"], log);
        var report = ParseReport(result);
        if (report.Elements().LastOrDefault(x => x.Name.LocalName == "summary")?.Attribute("status")?.Value != "valid" || report.Descendants().Any(x => x.Name.LocalName == "summary" && (string?)x.Attribute("status") != "valid")) throw new InvalidDataException("Mustang meldet kein gültiges Ergebnis.");
        Console.WriteLine("PASS " + Path.GetFileName(log));
    }
    static void ValidateSchema(string jar, string xml)
    {
        const string prefix = "schema/ZF_250/EN16931/";
        using var zip = ZipFile.OpenRead(jar);
        var schemas = new XmlSchemaSet { XmlResolver = null };
        var names = new List<string>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.EndsWith(".xsd")))
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            schemas.Add(null, reader);
            names.Add(entry.FullName);
        }
        if (names.Count != 4) throw new InvalidDataException("Die vier festgelegten lokalen EN16931-XSDs fehlen im Mustang-JAR.");
        schemas.Compile();
        var errors = new List<string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using (var reader = XmlReader.Create(xml, settings)) while (reader.Read()) { }
        if (errors.Count != 0) throw new InvalidDataException("XML-Schemaprüfung fehlgeschlagen: " + string.Join("; ", errors));
    }
    static XElement ParseReport(string text)
    {
        var start = text.IndexOf('<');
        if (start < 0) throw new InvalidDataException("Prüfer lieferte keinen XML-Bericht.");
        return XDocument.Parse(text[start..]).Root!;
    }
    static async Task<string> Run(string file, string[] args, string step)
    {
        var directory = Path.GetDirectoryName(step)!;
        var info = new ProcessStartInfo(file) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR" }) info.Environment[variable] = directory;
        if (Path.GetFileNameWithoutExtension(file) == "java")
            info.Environment["JDK_JAVA_OPTIONS"] = $"-Djava.io.tmpdir=\"{directory}\"";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Werkzeug konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new ToolFailure(Path.GetFileName(step), -1);
        }
        await stderr;
        var report = await stdout;
        if (process.ExitCode != 0) throw new ToolFailure(Path.GetFileName(step), process.ExitCode);
        return report;
    }

    sealed class ToolFailure(string stage, int exitCode) : Exception
    {
        internal string Stage { get; } = stage;
        internal int ExitCode { get; } = exitCode;
    }

    record Field(string Value, string Source, double[]? Region);
    record Snapshot(string Number, DateTime Date, DateTime Delivery, string Seller, string SellerStreet, string SellerPostcode, string SellerCity, string VatId, string Buyer, string BuyerStreet, string BuyerPostcode, string BuyerCity, string ItemName, string Description, decimal Quantity, decimal Price, decimal LineNet, decimal Rate, decimal Net, decimal Tax, decimal Gross, string Terms, Dictionary<string, Field> Provenance, PaymentDetails Payment);
}
