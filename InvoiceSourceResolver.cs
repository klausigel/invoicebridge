// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Kig.InvoiceBridge;

internal static class InvoiceSourceResolver
{
    internal static string Resolve(string directory, string invoiceNumber)
    {
        if (!Directory.Exists(directory))
            throw new BridgeException(10, "Quellverzeichnis nicht gefunden: " + directory);
        // Match a complete digit sequence, including Unicode digit boundaries.
        // Prefixes, suffixes and PDF extension case do not identify a customer.
        var number = new Regex(@"(?<!\d)" + Regex.Escape(invoiceNumber) + @"(?!\d)", RegexOptions.CultureInvariant);
        var matches = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                && number.IsMatch(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 0)
            throw new BridgeException(10, "Keine PDF mit vollständiger Rechnungsnummer " + invoiceNumber + " im Quellverzeichnis gefunden: " + directory);
        if (matches.Length > 1)
            throw new BridgeException(13, "Mehrere passende PDFs für Rechnung " + invoiceNumber + "; keine Auswahl vorgenommen:\n" + string.Join("\n", matches));
        return matches[0];
    }
}
