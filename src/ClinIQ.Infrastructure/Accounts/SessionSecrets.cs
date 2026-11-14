using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace ClinIQ.Infrastructure.Accounts;

public static class SessionSecrets
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(secret)));
    public static bool Matches(string secret, string hash) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(secret)), Convert.FromHexString(hash));
    public static string Format(Guid id, string secret) => $"{id:N}.{secret}";
    public static bool TryParse(string? token, out Guid id, out string secret)
    {
        id = default; secret = "";
        if (token is null || token.Length != 76 || token[32] != '.' ||
            !Guid.TryParseExact(token[..32], "N", out id) || id == Guid.Empty) return false;
        secret = token[33..];
        return secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
}
