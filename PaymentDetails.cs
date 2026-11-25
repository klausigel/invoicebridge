// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.General;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace Kig.InvoiceBridge;

internal sealed record PaymentDetails(string Iban, string Beneficiary, string Bic, string BankName)
{
    internal int Validate(string seller, bool optionalBic = false)
    {
        if (Beneficiary != seller) throw new InvalidDataException("Kontoinhaber und Rechnungssteller widersprechen sich.");
        if (!Regex.IsMatch(Iban, @"\A(?:DE[0-9]{20}|NL[0-9]{2}[A-Z]{4}[0-9]{10})\z"))
            throw new InvalidDataException("Erwartet wird eine deutsche oder niederländische IBAN im jeweiligen Länderformat.");
        var rotated = Iban[4..] + Iban[..4];
        int remainder = 0;
        foreach (var c in rotated)
        {
            var digits = char.IsAsciiDigit(c) ? c.ToString() : ((int)c - 'A' + 10).ToString(CultureInfo.InvariantCulture);
            foreach (var d in digits) remainder = (remainder * 10 + d - '0') % 97;
        }
        if (remainder != 1) throw new InvalidDataException("IBAN-Prüfsumme ungültig (Mod 97).");
        if (!(optionalBic && Bic.Length == 0) &&
            (!Regex.IsMatch(Bic, @"\A[A-Z]{4}(?:DE|NL)[A-Z0-9]{2}(?:[A-Z0-9]{3})?\z") ||
              Bic[4..6] != Iban[..2] || (Iban.StartsWith("NL", StringComparison.Ordinal) && Bic[..4] != Iban[4..8])))
            throw new InvalidDataException("BIC passt vom Format oder Bankcode nicht zur IBAN.");
        return remainder;
    }

    internal string Payload(decimal amount, string invoiceNumber, bool optionalBic = false)
    {
        Validate(Beneficiary, optionalBic);
        if (amount is < 0.01m or > 999999999.99m || decimal.Round(amount, 2) != amount) throw new InvalidDataException("GiroCode-Betrag außerhalb EPC-Grenzen.");
        if (string.IsNullOrWhiteSpace(Beneficiary) || Beneficiary.Length > 70 || Beneficiary.Any(char.IsControl) || string.IsNullOrWhiteSpace(invoiceNumber) || invoiceNumber.Length > 140 || invoiceNumber.Any(char.IsControl)) throw new InvalidDataException("GiroCode-Daten außerhalb EPC-Grenzen.");
        // EPC069-12 v3.1: Version 002; charset 1=UTF-8; BIC supplied voluntarily
        // for this German/EEA account; unstructured remittance = invoice number.
        // Purpose and structured RF-reference remain empty. No trailing LF.
        var payload = string.Join("\n", "BCD", "002", "1", "SCT", Bic, Beneficiary, Iban,
            "EUR" + amount.ToString("0.00", CultureInfo.InvariantCulture), "", "", invoiceNumber);
        if (Encoding.UTF8.GetByteCount(payload) > 331) throw new InvalidDataException("GiroCode überschreitet 331 Bytes.");
        return payload;
    }

    internal static BitMatrix EncodeQr(string payload)
    {
        var matrix = new MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> {
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.DISABLE_ECI] = true,
                [EncodeHintType.MARGIN] = 4
            });
        var modules = matrix.Width - 8; // Four white modules on each side.
        if (modules > 69 || matrix.Width != matrix.Height) throw new InvalidDataException("QR überschreitet EPC-Version 13.");
        return matrix;
    }

    // Top-left coordinates in points, specific to the exact approved sample.
    internal const int BankLeft = 430, BankTop = 272, BankRight = 574, BankBottom = 317;
    const double RenderScale = 150 / 72.0;
    const int QrAreaLeft = 430, QrAreaTop = 635, QrAreaRight = 548, QrAreaBottom = 762;

    internal void PreparePdf(string input, string output, decimal amount, string reference,
        bool includeGiroCode, RgbRaster? replacementRaster)
    {
        using var pdf = PdfDocument.Open(input);
        var builder = new PdfDocumentBuilder();
        var source = pdf.GetPage(1);
        var page = replacementRaster == null ? builder.AddPage(pdf, 1) : builder.AddPage(source.Width, source.Height);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        if (replacementRaster != null)
        {
            // Rebuild from pixels after removing the bank area. No original PDF object
            // with the old bank graphic survives underneath the replacement.
            var from = (int)Math.Floor(BankLeft * RenderScale);
            var to = (int)Math.Ceiling(BankRight * RenderScale);
            for (int y = (int)Math.Floor(BankTop * RenderScale); y < (int)Math.Ceiling(BankBottom * RenderScale); y++)
                replacementRaster.Data.AsSpan((y * replacementRaster.Width + from) * 3, (to - from) * 3).Fill(255);
            page.AddPng(replacementRaster.ToPng(), new PdfRectangle(0, 0, source.Width, source.Height));
            page.AddText("IBAN: " + Iban, 8, new PdfPoint(BankLeft + 2, source.Height - 295), font);
            page.AddText("BIC: " + Bic, 8, new PdfPoint(BankLeft + 2, source.Height - 307), font);
        }
        if (includeGiroCode)
            DrawGiroCode(page, font, builder.AddStandard14Font(Standard14Font.HelveticaBold), source.Height, amount, reference);
        File.WriteAllBytes(output, builder.Build());
    }

    void DrawGiroCode(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont bold,
        double pageHeight, decimal amount, string reference)
    {
        var matrix = EncodeQr(Payload(amount, reference));
        const double left = 433, top = 651.5, size = 108;
        const double frameLeft = 431.2, frameRight = 544.3;
        const string title = "GiroCode", hint = "Mit Banking App scannen";
        const double titleSize = 9, hintSize = 6.2;
        var bottom = pageHeight - top - size;
        var frameBottom = pageHeight - 761.2;
        var frameTop = pageHeight - 648.5;
        var frameCenter = (frameBottom + frameTop) / 2;
        var hintGlyphs = page.MeasureText(hint, hintSize, new PdfPoint(0, 0), font).Select(l => l.BoundingBox).ToArray();
        var hintLeft = hintGlyphs.Min(g => g.Left);
        var hintRight = hintGlyphs.Max(g => g.Right);
        var hintMidHeight = (hintGlyphs.Min(g => g.Bottom) + hintGlyphs.Max(g => g.Top)) / 2;
        var gap = (hintRight - hintLeft) / 2 + 3;
        var operations = page.CurrentStream.Operations;
        page.SetStrokeColor(0, 0, 0);
        DrawRoundedFrame(operations, frameLeft, frameBottom, frameRight, frameTop, frameCenter, gap);
        page.ResetColor();
        page.SetStrokeColor(255, 255, 255);
        page.SetTextAndFillColor(255, 255, 255);
        page.DrawRectangle(new PdfPoint(left, bottom), size, size, 0, true);
        // Each PdfPig color setter saves graphics state; balance both saves.
        page.ResetColor();
        page.ResetColor();
        page.SetTextAndFillColor(0, 0, 0);
        var step = size / matrix.Width;
        // Draw one fill on an integer module grid so antialiasing only affects the outer contour.
        operations.Add(Push.Value);
        operations.Add(new ModifyCurrentTransformationMatrix([step, 0, 0, step, left, bottom]));
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) operations.Add(new AppendRectangle(x, matrix.Height - y - 1, 1, 1));
        operations.Add(FillPathNonZeroWinding.Value);
        operations.Add(Pop.Value);
        var titleGlyphs = page.MeasureText(title, titleSize, new PdfPoint(0, 0), bold).Select(l => l.BoundingBox).ToArray();
        var titleMid = (titleGlyphs.Min(g => g.Left) + titleGlyphs.Max(g => g.Right)) / 2;
        page.AddText(title, titleSize, new PdfPoint((frameLeft + frameRight) / 2 - titleMid, pageHeight - 645), bold);
        operations.Add(Push.Value);
        operations.Add(new ModifyCurrentTransformationMatrix([0, 1, -1, 0, frameRight + hintMidHeight, frameCenter]));
        page.AddText(hint, hintSize, new PdfPoint(-(hintLeft + hintRight) / 2, 0), font);
        operations.Add(Pop.Value);
        page.ResetColor();
    }

    // Start below the hint, then go around all four corners to the upper end of the gap.
    static void DrawRoundedFrame(List<IGraphicsStateOperation> path,
        double left, double bottom, double right, double top, double center, double gap)
    {
        const double radius = 3.8, tangent = radius * 0.55228475;
        path.Add(new SetLineWidth(0.65));
        path.Add(new SetLineCap(1)); // Round caps at the two edges of the text gap.
        path.Add(new BeginNewSubpath(right, center - gap));
        path.Add(new AppendStraightLineSegment(right, bottom + radius));
        path.Add(new AppendDualControlPointBezierCurve(right, bottom + radius - tangent, right - radius + tangent, bottom, right - radius, bottom));
        path.Add(new AppendStraightLineSegment(left + radius, bottom));
        path.Add(new AppendDualControlPointBezierCurve(left + radius - tangent, bottom, left, bottom + radius - tangent, left, bottom + radius));
        path.Add(new AppendStraightLineSegment(left, top - radius));
        path.Add(new AppendDualControlPointBezierCurve(left, top - radius + tangent, left + radius - tangent, top, left + radius, top));
        path.Add(new AppendStraightLineSegment(right - radius, top));
        path.Add(new AppendDualControlPointBezierCurve(right - radius + tangent, top, right, top - radius + tangent, right, top - radius));
        path.Add(new AppendStraightLineSegment(right, center + gap));
        path.Add(StrokePath.Value);
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

    internal static void RequireBlankQrArea(RgbRaster input)
    {
        for (int y = (int)Math.Floor(QrAreaTop * RenderScale); y < Math.Ceiling(QrAreaBottom * RenderScale); y++)
            for (int x = (int)Math.Floor(QrAreaLeft * RenderScale); x < Math.Ceiling(QrAreaRight * RenderScale); x++)
            {
                var index = (y * input.Width + x) * 3;
                if (input.Data[index] < 250 || input.Data[index + 1] < 250 || input.Data[index + 2] < 250)
                    throw new BridgeException(11, "GiroCode-Bereich ist nicht frei.");
            }
    }

    internal void VerifyRendered(string originalPath, string resultPath, decimal amount, string reference,
        bool replaceGraphicBank, bool includeGiroCode)
    {
        var original = RgbRaster.Open(originalPath);
        var final = RgbRaster.Open(resultPath);
        if (original.Width != final.Width || original.Height != final.Height) throw new InvalidDataException("Gerenderte Seitengrößen stimmen nicht überein.");
        var rgb = final.Data;
        long changed = 0, formerQrAreaChanged = 0, formerQrAreaPixels = 0;
        for (var y = 0; y < final.Height; y++) for (var x = 0; x < final.Width; x++)
        {
            var offset = (y * final.Width + x) * 3;
            bool inQrArea = x >= QrAreaLeft * RenderScale && x <= QrAreaRight * RenderScale &&
                y >= QrAreaTop * RenderScale && y <= QrAreaBottom * RenderScale;
            if (includeGiroCode && inQrArea) continue;
            if (replaceGraphicBank && x >= Math.Floor(BankLeft * RenderScale) && x <= Math.Ceiling(BankRight * RenderScale)
                && y >= Math.Floor(BankTop * RenderScale) && y <= Math.Ceiling(BankBottom * RenderScale)) continue;
            int dr = Math.Abs(original.Data[offset] - rgb[offset]), dg = Math.Abs(original.Data[offset + 1] - rgb[offset + 1]), db = Math.Abs(original.Data[offset + 2] - rgb[offset + 2]);
            bool different = Math.Max(dr, Math.Max(dg, db)) > 16;
            if (different) changed++;
            if (inQrArea)
            {
                formerQrAreaPixels++;
                if (different) formerQrAreaChanged++;
            }
        }
        if ((double)changed / (final.Width * final.Height) > 0.005 ||
            !includeGiroCode && formerQrAreaChanged > 0.005 * formerQrAreaPixels)
            throw new InvalidDataException("Unerwartete Bildabweichung außerhalb der beabsichtigten Änderungen.");
        if (!includeGiroCode) return;
        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var decoded = reader.Decode(new RGBLuminanceSource(rgb, final.Width, final.Height, RGBLuminanceSource.BitmapFormat.RGB24));
        if (decoded == null || decoded.Text != Payload(amount, reference)) throw new InvalidDataException("GiroCode der finalen gerenderten PDF stimmt nicht mit den Zahlungsdaten überein.");
        decoded.ResultMetadata.TryGetValue(ResultMetadataType.ERROR_CORRECTION_LEVEL, out var ecc);
        if (ecc?.ToString() != "M") throw new InvalidDataException("GiroCode-Fehlerkorrektur ist nicht M.");
    }
}
