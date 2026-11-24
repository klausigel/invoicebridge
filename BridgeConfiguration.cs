// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kig.InvoiceBridge;

internal sealed record BridgeConfiguration
{
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string SourceFileSelection { get; init; }
    public string ToolsDirectory { get; init; } = "tools";
    public string? GhostscriptExecutable { get; init; }
    public required PaymentDetails Bank { get; init; }

    internal static (BridgeConfiguration Settings, string Path) Load(string? explicitPath)
    {
        var path = Path.GetFullPath(explicitPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "invoicebridge.json"));
        if (!File.Exists(path)) throw new BridgeException(2, "Konfigurationsdatei fehlt: " + path);
        BridgeConfiguration settings;
        try
        {
            settings = JsonSerializer.Deserialize<BridgeConfiguration>(File.ReadAllText(path), new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new JsonException("Leere Konfiguration.");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new BridgeException(2, "Konfiguration kann nicht gelesen werden: " + ex.Message); }
        if (settings.Bank == null || string.IsNullOrWhiteSpace(settings.SourceDirectory) || string.IsNullOrWhiteSpace(settings.OutputDirectory) || string.IsNullOrWhiteSpace(settings.ToolsDirectory) || string.IsNullOrWhiteSpace(settings.SourceFileSelection))
            throw new BridgeException(2, "Quell-/Zielverzeichnis, Dateimuster, Werkzeugverzeichnis und Bankdaten müssen vollständig sein.");
        if (settings.SourceFileSelection != "InvoiceNumberToken")
            throw new BridgeException(2, "SourceFileSelection muss InvoiceNumberToken sein (vollständige Rechnungsnummer im PDF-Dateinamen).");
        var basePath = System.IO.Path.GetDirectoryName(path)!;
        string Resolve(string value) => System.IO.Path.GetFullPath(value, basePath);
        try
        {
            var bank = settings.Bank with { Iban = settings.Bank.Iban.Replace(" ", "").ToUpperInvariant(), Bic = settings.Bank.Bic.Trim().ToUpperInvariant(), Beneficiary = settings.Bank.Beneficiary.Trim(), BankName = settings.Bank.BankName.Trim() };
            bank.Validate("NetProof GmbH");
            // Keep the approved exact sample scope, even though master data are
            // now read from JSON: silently changing the raster letterhead's
            // bank account would make the hybrid inconsistent.
            var approved = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", bank.Iban, bank.Beneficiary, bank.Bic, bank.BankName))));
            if (approved != ApprovedBankFingerprint) throw new InvalidDataException("Bankdaten entsprechen nicht den freigegebenen Stammdaten der Musterrechnung.");
            return (settings with {
                SourceDirectory = Resolve(settings.SourceDirectory),
                OutputDirectory = Resolve(settings.OutputDirectory),
                ToolsDirectory = Resolve(settings.ToolsDirectory),
                GhostscriptExecutable = settings.GhostscriptExecutable is null ? null : Resolve(settings.GhostscriptExecutable),
                Bank = bank
            }, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NullReferenceException)
        { throw new BridgeException(2, "Ungültige Konfiguration: " + ex.Message); }
    }

    internal const string ApprovedBankFingerprint = "02ED39B0D87013760781554BEA4AB73EC43606250579B939578DC15337E78D51";
}

internal sealed class BridgeException(int exitCode, string message) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}
