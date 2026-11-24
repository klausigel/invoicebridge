// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using ZXing;
using ZXing.QrCode.Internal;

namespace Kig.InvoiceBridge;

internal sealed record PaymentDetails(string Iban, string Beneficiary, string Bic, string BankName)
{
    internal int Validate(string seller)
    {
        if (Beneficiary != seller) throw new InvalidDataException("Kontoinhaber und Rechnungssteller widersprechen sich.");
        if (!Regex.IsMatch(Iban, "^DE[0-9]{20}$")) throw new InvalidDataException("Deutsche IBAN muss normalisiert 22 Zeichen enthalten.");
        var rotated = Iban[4..] + Iban[..4];
        int remainder = 0;
        foreach (var c in rotated)
        {
            var digits = char.IsAsciiDigit(c) ? c.ToString() : ((int)c - 'A' + 10).ToString(CultureInfo.InvariantCulture);
            foreach (var d in digits) remainder = (remainder * 10 + d - '0') % 97;
        }
        if (remainder != 1) throw new InvalidDataException("IBAN-Prüfsumme ungültig (Mod 97).");
        if (!Regex.IsMatch(Bic, "^[A-Z]{4}DE[A-Z0-9]{2}([A-Z0-9]{3})?$")) throw new InvalidDataException("BIC hat ein unerwartetes Format.");
        return remainder;
    }

    internal string Payload(decimal amount, string invoiceNumber)
    {
        Validate(Beneficiary);
        if (amount is < 0.01m or > 999999999.99m || decimal.Round(amount, 2) != amount) throw new InvalidDataException("GiroCode-Betrag außerhalb EPC-Grenzen.");
        if (Beneficiary.Length > 70 || string.IsNullOrWhiteSpace(invoiceNumber) || invoiceNumber.Length > 140 || invoiceNumber.Contains('\n') || invoiceNumber.Contains('\r')) throw new InvalidDataException("GiroCode-Daten außerhalb EPC-Grenzen.");
        // EPC069-12 v3.1: Version 002; charset 1=UTF-8; BIC supplied voluntarily
        // for this German/EEA account; unstructured remittance = invoice number.
        // Purpose and structured RF-reference remain empty. No trailing LF.
        var payload = string.Join("\n", "BCD", "002", "1", "SCT", Bic, Beneficiary, Iban,
            "EUR" + amount.ToString("0.00", CultureInfo.InvariantCulture), "", "", invoiceNumber);
        if (Encoding.UTF8.GetByteCount(payload) > 331) throw new InvalidDataException("GiroCode überschreitet 331 Bytes.");
        return payload;
    }

    internal void AddGiroCode(string input, string output, decimal amount, string reference)
    {
        var payload = Payload(amount, reference);
        var matrix = new MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> {
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.DISABLE_ECI] = true,
                [EncodeHintType.MARGIN] = 4
            });
        var modules = matrix.Width - 8; // Four white modules on each side.
        if (modules > 69 || matrix.Width != matrix.Height) throw new InvalidDataException("QR überschreitet EPC-Version 13.");
        using var pdf = PdfDocument.Open(input);
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(pdf, 1);
        const double left = 435, top = 650, size = 108;
        var bottom = pdf.GetPage(1).Height - top - size;
        page.SetStrokeColor(255, 255, 255);
        page.SetTextAndFillColor(255, 255, 255);
        page.DrawRectangle(new PdfPoint(left, bottom), size, size, 0, true);
        // Each PdfPig color setter saves graphics state; balance both saves.
        page.ResetColor();
        page.ResetColor();
        page.SetStrokeColor(0, 0, 0);
        page.SetTextAndFillColor(0, 0, 0);
        var step = size / matrix.Width;
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) page.DrawRectangle(new PdfPoint(left + x * step, bottom + (matrix.Height - y - 1) * step), step, step, 0, true);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("GiroCode - Rechnung " + reference, 7, new PdfPoint(left, bottom + size + 5), font);
        page.ResetColor();
        page.ResetColor();
        File.WriteAllBytes(output, builder.Build());
    }

    // QA helper: decode rasterized final PDF, not the in-memory encoding matrix.
    internal static int DecodeGray(string path, int width, int height)
    {
        var pixels = File.ReadAllBytes(path);
        if (pixels.Length != checked(width * height)) throw new InvalidDataException("Falsche Graubildgröße.");
        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var result = reader.Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8))
            ?? throw new InvalidDataException("Kein QR-Code im gerenderten Bild dekodierbar.");
        result.ResultMetadata.TryGetValue(ResultMetadataType.ERROR_CORRECTION_LEVEL, out var errorCorrection);
        Console.WriteLine(JsonSerializer.Serialize(new { payload = result.Text, format = result.BarcodeFormat.ToString(),
            errorCorrection = errorCorrection?.ToString() }));
        return 0;
    }

    internal void VerifyRendered(string originalPath, string resultPath, decimal amount, string reference)
    {
        var original = RgbRaster.Open(originalPath);
        var final = RgbRaster.Open(resultPath);
        if (original.Width != final.Width || original.Height != final.Height) throw new InvalidDataException("Gerenderte Seitengrößen stimmen nicht überein.");
        var rgb = final.Data;
        long changed = 0;
        for (var y = 0; y < final.Height; y++) for (var x = 0; x < final.Width; x++)
        {
            var offset = (y * final.Width + x) * 3;
            // Exclude only the intentional QR and caption, in points from top-left.
            if (x >= 430 * 150 / 72.0 && x <= 548 * 150 / 72.0 && y >= 635 * 150 / 72.0 && y <= 762 * 150 / 72.0) continue;
            int dr = Math.Abs(original.Data[offset] - rgb[offset]), dg = Math.Abs(original.Data[offset + 1] - rgb[offset + 1]), db = Math.Abs(original.Data[offset + 2] - rgb[offset + 2]);
            if (Math.Max(dr, Math.Max(dg, db)) > 16) changed++;
        }
        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var decoded = reader.Decode(new RGBLuminanceSource(rgb, final.Width, final.Height, RGBLuminanceSource.BitmapFormat.RGB24));
        if (decoded == null || decoded.Text != Payload(amount, reference)) throw new InvalidDataException("GiroCode der finalen gerenderten PDF stimmt nicht mit den Zahlungsdaten überein.");
        decoded.ResultMetadata.TryGetValue(ResultMetadataType.ERROR_CORRECTION_LEVEL, out var ecc);
        if (ecc?.ToString() != "M") throw new InvalidDataException("GiroCode-Fehlerkorrektur ist nicht M.");
        var fraction = (double)changed / (final.Width * final.Height);
        if (fraction > 0.005) throw new InvalidDataException("Unerwartete Bildabweichung außerhalb des GiroCode-Bereichs.");
    }
}
