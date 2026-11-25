// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Kig.InvoiceBridge;

// Only the reviewed one-line KIG commission invoice. The Dutch account and
// seller name must both match the authenticated local profile.
internal static class KigInvoice
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    const string Money = @"(?:[0-9]{1,9}|[0-9]{1,3}(?:\.[0-9]{3}){1,2}),[0-9]{2}";
    const string Bank = @"^Kontoinhaber:\s*(?<holder>.+?)\s*·\s*IBAN:\s*(?<iban>NL[0-9]{2}[ A-Z0-9]+?)\s*·\s*BIC:\s*(?<bic>[A-Z]{4}NL[A-Z0-9]{2}(?:[A-Z0-9]{3})?)$";
    const string Immediate = "Der Rechnungsbetrag ist sofort fällig. Bitte überweisen Sie den Betrag auf folgendes Konto:";

    internal static Program.Snapshot? Read(string file, PaymentDetails payment, bool authorized)
    {
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(file); }
        catch { return null; }
        using (pdf)
        {
            if (pdf.NumberOfPages < 1) return null;
            var page = pdf.GetPage(1);
            var words = page.GetWords().ToArray();
            string[] Rows(double l, double t, double r, double b) => Lines(words.Where(w =>
                w.BoundingBox.Left >= l && w.BoundingBox.Right <= r &&
                page.Height - w.BoundingBox.Top >= t && page.Height - w.BoundingBox.Bottom <= b));
            bool Label(string pattern, double top, double bottom) =>
                Rows(50, top, 560, bottom).Any(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            if (!Label(@"^Rechnungsnr\s*\.", 305, 330) ||
                !Label(@"^Anzahl\s+Artikel", 373, 403) || !Label(@"^Nettobetrag\s+in\s+EUR", 442, 465))
                return null;
            if (!authorized) throw new BridgeException(14, "Diese Rechnungsvorlage ist dem angemeldeten Mandanten nicht zugeordnet.");
            if (pdf.NumberOfPages != 1)
                throw Bad("Die KIG-Datei enthält mehrere Seiten. Bitte eine einzelne Rechnung hochladen.");
            if (Math.Abs(page.Width - 595) > 1 || Math.Abs(page.Height - 842) > 1 ||
                pdf.Advanced.TryGetEmbeddedFiles(out var embedded) && embedded.Count != 0)
                throw Bad("Unterstützt wird eine normale einseitige KIG-PDF ohne eingebettete Dateien.");

            string One(double l, double t, double r, double b, string area)
            {
                var rows = Rows(l, t, r, b);
                if (rows.Length != 1) throw Bad("Der Bereich „" + area + "“ ist nicht eindeutig.");
                return rows[0];
            }
            Match Match(string pattern, string text, string area)
            {
                var result = Regex.Match(text, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (!result.Success) throw Bad("Der Bereich „" + area + "“ entspricht nicht der KIG-Einzelvorlage.");
                return result;
            }
            decimal Amount(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, German);
            (string Postal, string City) City(string line, bool explicitCountry)
            {
                var m = Match((explicitCountry ? @"^DE(?:\s*[-–]\s*|\s+)" : "^") + @"([0-9]{5})\s+([^\d]+)$", line, "Anschrift");
                return (m.Groups[1].Value, m.Groups[2].Value);
            }

            var number = Match(@"^Rechnungsnr\s*\.\s*:\s*([0-9]{8}-[0-9]{5})$", One(55,304,565,329,"Rechnungsnummer"),"Rechnungsnummer").Groups[1].Value;
            if (!Rows(55,280,150,309).Any(line => line == "Rechnung") ||
                !DateTime.TryParseExact(One(470,280,565,309,"Rechnungsdatum"), "dd.MM.yyyy",
                    German, DateTimeStyles.None, out var date))
                throw Bad("Das Rechnungsdatum ist nicht eindeutig.");
            var printedReference = Match(@"^Referenz\s*:\s*(.{1,200})$", One(55,325,565,362,"Referenz"),"Referenz").Groups[1].Value;
            var sender = One(55,139,566,165,"Absender").Split('·', StringSplitOptions.TrimEntries);
            var footer = One(55,769,575,793,"Aussteller im Fußbereich").Split('·', StringSplitOptions.TrimEntries);
            if (sender.Length != 3 || footer.Length != 3 ||
                !string.Equals(sender[0], footer[0], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(StreetKey(sender[1]), StreetKey(footer[1]), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sender[2], footer[2], StringComparison.OrdinalIgnoreCase))
                throw Bad("Aussteller oder Anschrift sind nicht eindeutig zugeordnet.");
            var (sellerPost, sellerCity) = City(sender[2], false);
            var buyer = Rows(55,162,390,250);
            if (buyer is not ["Firma", "NetProof GmbH", _, _])
                throw Bad("Die Empfängeranschrift entspricht nicht der unterstützten KIG-Einzelvorlage.");
            var (buyerPost, buyerCity) = City(buyer[3], true);

            var heading = string.Join(' ', Rows(55,377,565,400));
            Match(@"^Anzahl Artikel Einzelpreis\s*/\s*EUR Gesamtpreis\s*/\s*EUR$", heading, "Positionskopf");
            var body = Rows(55,398,565,439);
            if (body.Length != 2) throw Bad("Erwartet wird genau eine Position mit dem ausgewiesenen Erlösanteil.");
            var quantity = Match(@"^([1-9][0-9]{0,5})$", One(55,399,94,420,"Menge"),"Menge");
            var item = One(96,399,392,422,"Artikel");
            var price = Amount(Match("^"+Money+"$", One(396,399,488,422,"Einzelpreis"),"Einzelpreis").Value);
            var line = Amount(Match("^"+Money+"$", One(495,399,565,422,"Positionsbetrag"),"Positionsbetrag").Value);
            var description = Match(@"^50\s*% vom Verkaufserlös$", body[1], "Erlösanteil").Value;
            decimal Total(string pattern, int top, string area) =>
                Amount(Match(pattern + @"\s*:\s*(" + Money + ")$", One(300,top,565,top+21,area),area).Groups[1].Value);
            var net = Total(@"^Nettobetrag in EUR",445,"Netto");
            var tax = Total(@"^zzgl\.\s*19\s*%\s*Mwst\.",460,"Umsatzsteuer");
            var gross = Total(@"^Rechnungsbetrag in EUR",475,"Brutto");
            var count = decimal.Parse(quantity.Value,CultureInfo.InvariantCulture);
            if (price <= 0 || count * price != line || line != net ||
                decimal.Round(net * .19m,2,MidpointRounding.AwayFromZero) != tax || net + tax != gross)
                throw Bad("Positionsbetrag, Umsatzsteuer oder Rundung stimmen nicht überein.");
            if (One(50,511,575,535,"Fälligkeit") != Immediate)
                throw Bad("Die Fälligkeit oder Zahlungsart ist nicht eindeutig.");
            if (Rows(50,495,575,511).Length != 0 || Rows(50,592,575,769).Any(row =>
                    Regex.IsMatch(row, @"(?i)Leistungs(?:datum|zeit|zeitraum)|Lieferdatum|\b(?:bezahlt|Vorauszahlung)\b")))
                throw Bad("Eine zusätzliche Leistungs- oder Zahlungsaussage wird in dieser Vorlage nicht unterstützt.");

            var bankWords = words.Where(w => page.Height-w.BoundingBox.Top >= 531 && page.Height-w.BoundingBox.Bottom <= 555).ToArray();
            bool AcrossBank(PdfRectangle b) => page.Height-b.Top < 555 && page.Height-b.Bottom > 531;
            if (bankWords.Any(w => w.Letters.Any(l => l.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)) ||
                page.GetImages().Any(img => AcrossBank(img.BoundingBox)) ||
                page.Paths.Any(path => path.IsFilled && path.GetBoundingRectangle() is {} bounds && AcrossBank(bounds)))
                throw Bad("Der KIG-Bankblock muss sichtbar und eindeutig als Text vorliegen.");
            var bankLine = One(50,531,575,555,"Bankverbindung");
            var printed = Match(Bank, bankLine, "Bankverbindung");
            var allText = string.Join('\n', Lines(words));
            if (Regex.Matches(allText, @"\b(?:IBAN|BIC|SWIFT|Kontoinhaber)\s*:", RegexOptions.IgnoreCase).Count != 3 ||
                Regex.Matches(allText, @"(?<!\w)(?:NL[0-9]{2}(?:\s*[A-Z0-9]){14}|DE[0-9]{2}(?:\s*[0-9]){18})(?!\w)",RegexOptions.IgnoreCase).Count != 1)
                throw Bad("Mehrere oder unklare Bankangaben gefunden.");
            var iban = string.Concat(printed.Groups["iban"].Value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
            var bic = printed.Groups["bic"].Value.ToUpperInvariant();
            var holder = printed.Groups["holder"].Value;
            if (!string.Equals(holder, sender[0], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(holder, payment.Beneficiary, StringComparison.OrdinalIgnoreCase) ||
                iban != payment.Iban || BicComparisonKey(bic) != BicComparisonKey(payment.Bic))
                throw new BridgeException(15, "Die lesbare Bankverbindung oder der Kontoinhaber widerspricht dem KIG-Profil.");
            var visiblePayment = payment with { Iban = iban, Bic = bic, Beneficiary = sender[0] };
            visiblePayment.Validate(sender[0]);
            var vat = Match(@"^Umsatzsteuer-Identifikationsnummer\s*:\s*(DE[0-9]{9})$",
                One(50,557,575,582,"Umsatzsteuer-ID"),"Umsatzsteuer-ID").Groups[1].Value;
            var fiscal = Match(@"\bSteuernummer\s*:\s*([0-9]{2}/[0-9]{3}/[0-9]{5})$",
                One(55,791,575,815,"Steuernummer"),"Steuernummer").Groups[1].Value;

            return new Program.Snapshot(number, date, null, sender[0], footer[1], sellerPost, sellerCity, vat,
                buyer[1], buyer[2], buyerPost, buyerCity, item, description, count, price, line, 19, net, tax, gross,
                "Sofort fällig per Überweisung.", new(), visiblePayment)
            {
                DueDate = date, PrintedReference = printedReference, FiscalNumber = fiscal
            };
        }
    }

    static string BicComparisonKey(string bic) =>
        bic.Length == 11 && bic.EndsWith("XXX", StringComparison.Ordinal) ? bic[..8] : bic;

    static string StreetKey(string street) =>
        Regex.Replace(street, @"(?:str\.|straße)(?=\s+[0-9])", "strasse", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string[] Lines(IEnumerable<Word> words)
    {
        var rows = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < 3.1);
            if (row == null) rows.Add([word]);
            else row.Add(word);
        }
        return rows.Select(r => string.Join(' ',r.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))).ToArray();
    }
    static BridgeException Bad(string message) => new(11, message);
}
