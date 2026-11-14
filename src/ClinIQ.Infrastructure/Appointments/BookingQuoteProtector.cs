using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ClinIQ.Infrastructure.Appointments;

internal sealed record BookingQuote(Guid PatientId, Guid SessionId, Guid? SourceId, string SelectionHash, string OverlapHash, DateTimeOffset ExpiresAtUtc);
internal sealed class BookingQuoteProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ClinIQ.BookingQuote.v1");
    public string Protect(BookingQuote quote) => _protector.Protect(JsonSerializer.Serialize(quote));
    public BookingQuote? Read(string token)
    {
        try { return JsonSerializer.Deserialize<BookingQuote>(_protector.Unprotect(token)); }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException or ArgumentException) { return null; }
    }
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
