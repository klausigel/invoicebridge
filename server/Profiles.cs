// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kig.InvoiceBridge;

internal sealed record Profile(string Id, byte[] KeyHash, PaymentDetails Bank);

internal sealed record ProfileFile(string Id, string ApiKey, PaymentDetails Bank);

internal static class Profiles
{
    internal static Profile[] Load(string file)
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
            var bank = p.Bank with { Iban = p.Bank.Iban.Replace(" ", "").ToUpperInvariant(),
                Bic = p.Bank.Bic.Trim().ToUpperInvariant(), Beneficiary = p.Bank.Beneficiary.Trim(),
                BankName = p.Bank.BankName.Trim() };
            // The local exercise uses explicitly non-payable German test accounts.
            bank.Validate("NetProof GmbH");
            if (bank.Iban[4..12] != "00000000" || string.IsNullOrWhiteSpace(bank.BankName))
                throw new InvalidDataException();
            return new Profile(p.Id, Digest(p.ApiKey), bank);
        }).ToArray();
        if (result[0].Bank.Iban == result[1].Bank.Iban || result[0].Bank.Bic == result[1].Bank.Bic)
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
