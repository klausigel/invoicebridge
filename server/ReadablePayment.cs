// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kig.InvoiceBridge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

internal static class ReadablePayment
{
    internal static bool Conflicts(string input, PaymentDetails bank)
    {
        using var pdf = PdfDocument.Open(input);
        if (pdf.NumberOfPages != 1) throw new InvalidDataException();
        var text = ContentOrderTextExtractor.GetText(pdf.GetPage(1));
        foreach (Match match in Regex.Matches(text, @"(?<![A-Z0-9])DE(?:[ \t]*[0-9]){20}(?![ \t]*[0-9])", RegexOptions.IgnoreCase))
            if (!Same(Compact(match.Value), bank.Iban)) return true;
        foreach (Match match in Regex.Matches(text, @"\b(?<label>IBAN|BIC|SWIFT|Kontoinhaber|Bankname)(?:[ \t]*:[ \t]*|[ \t]+)(?<value>[^\r\n]+)", RegexOptions.IgnoreCase))
        {
            var label = match.Groups["label"].Value.ToUpperInvariant();
            var value = match.Groups["value"].Value.Trim();
            var consistent = label switch
            {
                "IBAN" => Same(Compact(value), bank.Iban),
                "BIC" or "SWIFT" => Same(Compact(value), bank.Bic),
                "KONTOINHABER" => Same(value, bank.Beneficiary),
                _ => Same(value, bank.BankName)
            };
            if (!consistent) return true;
        }
        return false;
    }

    static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
