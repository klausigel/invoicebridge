// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kig.InvoiceBridge;

internal enum ProfileMode { Synthetic, LocalReal }

internal sealed record Profile(string Id, byte[] KeyHash, PaymentDetails Bank, string? ApprovedGraphicSha256, ProfileMode Mode);

internal sealed record ProfileFile(string Id, string ApiKey, PaymentDetails Bank, string? ApprovedGraphicSha256);

internal static class Profiles
{
    internal static ProfileMode LocalMode() => Environment.GetEnvironmentVariable("INVOICEBRIDGE_PROFILE_MODE") switch
    {
        null or "" or "synthetic" => ProfileMode.Synthetic,
        "local-real" => ProfileMode.LocalReal,
        _ => throw new InvalidDataException("Unbekannter Profilmodus. Erlaubt sind synthetic und local-real.")
    };

    internal static Profile[] Load(string file, ProfileMode mode)
    {
        var source = JsonSerializer.Deserialize<ProfileFile[]>(File.ReadAllText(file),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException();
        if (source.Length != 2 || !source.Select(p => p.Id).ToHashSet().SetEquals(["KIG", "Manuel"])
            || source.Select(p => p.ApiKey).Distinct(StringComparer.Ordinal).Count() != source.Length
            || source.Any(p => p.ApiKey == null || p.ApiKey.Length < 32 || p.ApiKey.Length > 128
                || p.ApiKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')))
            throw new InvalidDataException();

        var result = source.Select(p =>
        {
            var bank = p.Bank with { Iban = string.Concat(p.Bank.Iban.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant(),
                Bic = p.Bank.Bic.Trim().ToUpperInvariant(), Beneficiary = p.Bank.Beneficiary.Trim(),
                BankName = p.Bank.BankName.Trim() };
            // Keep the existing synthetic profile restrictions. Local profiles undergo
            // the same syntax and EPC checks, while the parser owns seller matching.
            if (mode == ProfileMode.Synthetic)
                bank.Validate("NetProof GmbH");
            else
                _ = bank.Payload(0.01m, "Profilpruefung");
            var germanPlaceholder = bank.Iban.StartsWith("DE", StringComparison.Ordinal) && bank.Iban[4..12] == "00000000";
            if ((mode == ProfileMode.Synthetic && !germanPlaceholder) ||
                (mode == ProfileMode.LocalReal && (germanPlaceholder || p.Id != "KIG" && bank.Iban.StartsWith("NL", StringComparison.Ordinal))) ||
                string.IsNullOrWhiteSpace(bank.BankName))
                throw new InvalidDataException();
            var graphic = p.ApprovedGraphicSha256?.ToUpperInvariant();
            if (graphic != null && (graphic.Length != 64 || !graphic.All(char.IsAsciiHexDigit)))
                throw new InvalidDataException();
            return new Profile(p.Id, Digest(p.ApiKey), bank, graphic, mode);
        }).ToArray();
        if (result[0].Bank.Iban == result[1].Bank.Iban ||
            (mode == ProfileMode.Synthetic && result[0].Bank.Bic == result[1].Bank.Bic))
            throw new InvalidDataException();
        return result;
    }

    internal static Profile? Authenticate(Profile[] profiles, string? key)
    {
        if (key is null || key.Length > 128) return null;
        var digest = Digest(key);
        Profile? match = null;
        foreach (var profile in profiles)
            if (CryptographicOperations.FixedTimeEquals(profile.KeyHash, digest)) match = profile;
        return match;
    }

    static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
