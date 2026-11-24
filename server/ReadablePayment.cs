// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kig.InvoiceBridge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

internal sealed record PaymentDecision(PaymentDetails? Payment, bool ReplaceGraphicBank, string? Error);

internal static class ReadablePayment
{
    static readonly Regex Iban = new(@"(?<![A-Z0-9])[A-Z]{2}[ \t]*[0-9]{2}(?:[ \t]*[A-Z0-9]){11,30}(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Field = new(@"\b(?<label>IBAN|BIC|SWIFT|Kontoinhaber|Bankname)(?:[ \t]*:[ \t]*|[ \t]+)(?<value>[^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static PaymentDecision Decide(string input, Profile profile)
    {
        using var pdf = PdfDocument.Open(input);
        if (pdf.NumberOfPages != 1) throw new InvalidDataException();
        var page = pdf.GetPage(1);
        if (Math.Abs(page.Width - 595) > 1 || Math.Abs(page.Height - 842) > 1) throw new InvalidDataException();
        var text = ContentOrderTextExtractor.GetText(page);
        var accounts = Iban.Matches(text).Select(m => Compact(m.Value)).ToArray();
        var fields = Field.Matches(text);
        static string Label(Match m) => m.Groups["label"].Value.ToUpperInvariant();
        static string Value(Match m) => m.Groups["value"].Value.Trim();
        var bicFields = fields.Where(m => Label(m) is "BIC" or "SWIFT").ToArray();
        var ibanFields = fields.Where(m => Label(m) == "IBAN").ToArray();
        if (accounts.Length > 1 || bicFields.Length > 1 || ibanFields.Length > 1
            || fields.Count(m => Label(m) == "KONTOINHABER") > 1 || fields.Count(m => Label(m) == "BANKNAME") > 1)
            return Reject("payment_ambiguous");

        var bank = profile.Bank;
        foreach (var account in accounts)
            if (!Same(account, bank.Iban)) return Reject("payment_conflict");
        foreach (Match field in fields)
        {
            var value = Value(field);
            if (!Same(Label(field) switch {
                    "IBAN" or "BIC" or "SWIFT" => Compact(value), _ => value
                }, Label(field) switch {
                    "IBAN" => bank.Iban, "BIC" or "SWIFT" => bank.Bic,
                    "KONTOINHABER" => bank.Beneficiary, _ => bank.BankName
                }))
                return Reject("payment_conflict");
        }

        var imagesInBlock = page.GetImages().Any(image => Intersects(image.BoundingBox, page.Height));
        if (accounts.Length == 1 && ibanFields.Length == 1 && bicFields.Length == 1)
        {
            var (block, visible) = ReadBlock(page);
            var covered = page.Paths.Any(path => path.IsFilled && path.GetBoundingRectangle() is { } bounds && Intersects(bounds, page.Height));
            var blockAccount = Iban.Match(block);
            if (!visible || covered || imagesInBlock || Iban.Matches(block).Count != 1 || Field.Matches(block).Count != fields.Count
                || !blockAccount.Success || !Same(Compact(blockAccount.Value), accounts[0])
                || !Field.Matches(block).Any(m => Label(m) is "BIC" or "SWIFT"))
                return Reject("payment_unclear");
            return new(bank with { Iban = accounts[0].ToUpperInvariant(), Bic = Compact(Value(bicFields[0])).ToUpperInvariant() }, false, null);
        }

        if (imagesInBlock && accounts.Length == 0 && fields.Count == 0 && IsApprovedGraphic(input, profile))
            return new(bank, true, null);
        return Reject("payment_unclear");
    }

    static bool IsApprovedGraphic(string input, Profile profile)
    {
        var hash = Kig.InvoiceBridge.Program.Hash(input);
        return (profile.Id == "Manuel" && hash == Kig.InvoiceBridge.Program.SampleHash) || hash == profile.ApprovedGraphicSha256;
    }

    static PaymentDecision Reject(string code) => new(null, false, code);
    internal static bool HasBankText(string file)
    {
        using var pdf = PdfDocument.Open(file);
        var text = ContentOrderTextExtractor.GetText(pdf.GetPage(1));
        return Iban.IsMatch(text) || Field.IsMatch(text);
    }
    static bool Intersects(PdfRectangle box, double height) =>
        box.Right > PaymentDetails.BankLeft && box.Left < PaymentDetails.BankRight &&
        height - box.Bottom > PaymentDetails.BankTop && height - box.Top < PaymentDetails.BankBottom;

    static (string Text, bool Visible) ReadBlock(Page page)
    {
        var lines = new List<List<Word>>();
        bool visible = true;
        foreach (var word in page.GetWords().Where(w => w.BoundingBox.Left >= PaymentDetails.BankLeft
                     && w.BoundingBox.Right <= PaymentDetails.BankRight
                     && page.Height - w.BoundingBox.Top >= PaymentDetails.BankTop
                     && page.Height - w.BoundingBox.Bottom <= PaymentDetails.BankBottom)
                 .OrderByDescending(w => w.BoundingBox.Bottom))
        {
            if (word.Letters.Any(letter => letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip))
                visible = false;
            var line = lines.FirstOrDefault(row => Math.Abs(row[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < 2);
            if (line == null) lines.Add([word]);
            else line.Add(word);
        }
        return (string.Join('\n', lines.Select(row => string.Join(' ', row.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))), visible);
    }

    static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
