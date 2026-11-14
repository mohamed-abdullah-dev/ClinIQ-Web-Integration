using System.Text.Json;
using ClinIQ.Application.Features.Appointments;
using ClinIQ.Domain.Appointments;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Appointments;

internal sealed class BookingNoticeDelivery(AppDbContext db, RegistrationStorage storage, IHostEnvironment environment, TimeProvider clock, ILogger<BookingNoticeDelivery> logger)
{
    public async Task<BookingNoticeRetryResult> DeliverAsync(Guid[] ids, CancellationToken ct)
    {
        var attempted = 0; var written = 0; var failedBeforeClaim = 0;
        foreach (var id in ids)
        {
            Guid? activeClaim = null;
            try
            {
                var now = clock.GetUtcNow(); var claim = Guid.NewGuid();
                var claimed = await db.BookingNotices.Where(n => n.Id == id && n.DeliveryStatus != "DevelopmentFile" &&
                    (n.LastAttemptUtc == null || n.LastAttemptUtc <= now.AddMinutes(-1)))
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastAttemptUtc, now).SetProperty(n => n.ClaimId, claim)
                        .SetProperty(n => n.Attempts, n => n.Attempts + 1).SetProperty(n => n.DeliveryStatus, "Sending"), ct);
                if (claimed == 0) continue;
                activeClaim = claim;
                attempted++;
                var notice = await db.BookingNotices.AsNoTracking().SingleAsync(n => n.Id == id, ct);
                var email = await db.Users.AsNoTracking().Where(u => u.Id == notice.RecipientUserId && u.EmailConfirmed).Select(u => u.Email).SingleOrDefaultAsync(ct);
                var status = "Unavailable";
                if (environment.IsDevelopment() && email is not null)
                {
                    var folder = Path.Combine(storage.Root, "development-booking-notices"); System.IO.Directory.CreateDirectory(folder);
                    var replacement = await db.Appointments.Where(a => a.ReplacesAppointmentId == notice.AppointmentId).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct);
                    // A stable filename makes private development delivery idempotent; no claim of real email delivery.
                    var path = Path.Combine(folder, id.ToString("N") + ".json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { To = email, NoticeId = id, Kind = notice.Kind.ToString(), notice.AppointmentId, ReplacementAppointmentId = replacement }), ct);
                    status = "DevelopmentFile";
                }
                await db.BookingNotices.Where(n => n.Id == id && n.ClaimId == claim).ExecuteUpdateAsync(s => s.SetProperty(n => n.DeliveryStatus, status), ct);
                if (status == "DevelopmentFile") written++;
            }
            catch (Exception e)
            {
                if (!activeClaim.HasValue) failedBeforeClaim++;
                if (activeClaim.HasValue)
                {
                    try { await db.BookingNotices.Where(n => n.Id == id && n.ClaimId == activeClaim).ExecuteUpdateAsync(s => s.SetProperty(n => n.DeliveryStatus, "Unavailable"), CancellationToken.None); }
                    catch (Exception) { /* A stale Sending claim remains eligible for retry after its lease. */ }
                }
                // The committed appointment and durable Pending/Sending row survive SQL/filesystem/cancellation failures.
                logger.LogWarning("Booking notice {NoticeId} remains retryable after {ExceptionType}.", id, e.GetType().Name);
            }
        }
        return new(attempted + failedBeforeClaim, written, attempted - written + failedBeforeClaim);
    }
}
