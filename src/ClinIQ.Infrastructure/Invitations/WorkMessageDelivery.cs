using System.Text.Json;
using ClinIQ.Infrastructure.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Invitations;

public sealed class WorkMessageOptions
{
    public string? Url { get; set; }
}
internal sealed class WorkMessageDelivery(RegistrationStorage storage, IHostEnvironment environment,
    Microsoft.Extensions.Options.IOptions<WorkMessageOptions> options, ILogger<WorkMessageDelivery> logger)
{
    public Task<string> InvitationAsync(Guid id, string email, string secret, string terms, decimal? fee, string? currency, DateTimeOffset expires, CancellationToken ct)
    {
        var url = options.Value.Url;
        if (url is null) return Task.FromResult("Unavailable");
        // A fragment keeps the secret out of HTTP request URLs and ordinary server access logs.
        var link = url.TrimEnd('/') + $"#id={id:D}&secret={Uri.EscapeDataString(secret)}";
        return WriteAsync(new
        {
            To = email,
            Subject = "ClinIQ work invitation",
            InvitationId = id,
            InvitationUrl = link,
            Terms = terms,
            ConsultationFee = fee,
            Currency = currency,
            ExpiresAtUtc = expires
        }, ct);
    }
    public Task<string> FeeAsync(Guid noticeId, string email, Guid locationId, decimal previous, decimal next, CancellationToken ct) =>
        WriteAsync(new { To = email, Subject = "ClinIQ consultation fee changed", NoticeId = noticeId, LocationId = locationId, PreviousFee = previous, NewFee = next, Currency = "EGP" }, ct);
    private async Task<string> WriteAsync<T>(T payload, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) return "Unavailable";
        var path = Path.Combine(storage.Root, "development-work-messages", Guid.NewGuid().ToString("N") + ".json"); var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true); created = true;
            await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct); return "DevelopmentFile";
        }
        catch (Exception e)
        {
            logger.LogWarning("Work message delivery failed with {ExceptionType}.", e.GetType().Name);
            if (created)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { logger.LogWarning("Partial work message cleanup failed with {ExceptionType}.", cleanup.GetType().Name); }
            }
            return "Unavailable";
        }
    }
}
