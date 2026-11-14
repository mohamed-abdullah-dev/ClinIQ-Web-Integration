using ClinIQ.Application.Features.Notifications;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Notifications;
internal sealed class FeedbackDispatcher(AppDbContext db, WorkforceAccess locks, TimeProvider clock,
    IDataProtectionProvider protection, IFeedbackNotificationTransport transport) : IFeedbackDispatcher
{
    public async Task<FeedbackDispatchResult> RunAsync(CancellationToken ct)
    {
        locks.Fresh(); var now = clock.GetUtcNow(); var expanded = 0; var attempted = 0; var accepted = 0; var deferred = 0; var cancelled = 0;
        // Expired/revoked accounts lose their binding, including token material. Revocation also detaches atomically via SQL trigger.
        await db.PushDevices.Where(d => d.UserId != null && !db.AuthSessions.Any(s => s.Id == d.SessionId && s.UserId == d.UserId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now &&
            db.Users.Any(u => u.Id == s.UserId && u.EmailConfirmed && u.SecurityStamp == s.SecurityStamp) && db.UserRoles.Any(ur => ur.UserId == s.UserId && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "PATIENT"))))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UserId, (Guid?)null).SetProperty(d => d.SessionId, (Guid?)null).SetProperty(d => d.TokenHash, (string?)null).SetProperty(d => d.ProtectedToken, (string?)null)
                .SetProperty(d => d.BindingRevision, d => d.BindingRevision + 1).SetProperty(d => d.ChangedAtUtc, now), ct);
        var notices = await db.FeedbackNotices.AsNoTracking().Where(n => n.ExpandedAtUtc == null && n.CancelledAtUtc == null && n.NextAttemptAtUtc <= now).OrderBy(n => n.NextAttemptAtUtc).ThenBy(n => n.Id).Select(n => n.Id).Take(20).ToArrayAsync(ct);
        foreach (var id in notices)
        {
            db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await locks.LockAsync($"ClinIQ.FeedbackNotice.{id:N}", ct)) continue;
            var notice = await db.FeedbackNotices.SingleAsync(n => n.Id == id, ct);
            if (notice.ExpandedAtUtc != null || notice.CancelledAtUtc != null || notice.NextAttemptAtUtc > clock.GetUtcNow()) continue;
            var request = await db.AttendanceFeedbackRequests.AsNoTracking().SingleAsync(r => r.Id == notice.FeedbackRequestId, ct);
            if (!await ApplicableAsync(request.Id, clock.GetUtcNow(), ct)) notice.Cancel(clock.GetUtcNow());
            else if (notice.Kind == FeedbackNoticeKind.EmailReminder)
            { db.FeedbackDeliveries.Add(FeedbackDelivery.Create(notice, request.PatientUserId)); notice.Expand(clock.GetUtcNow()); expanded++; }
            else
            {
                var devices = await db.PushDevices.AsNoTracking().Where(d => d.UserId == request.PatientUserId).OrderBy(d => d.Id).Take(100).ToArrayAsync(ct);
                if (devices.Length == 0) notice.WaitForDevice(clock.GetUtcNow());
                else { foreach (var device in devices) db.FeedbackDeliveries.Add(FeedbackDelivery.Create(notice, request.PatientUserId, device)); notice.Expand(clock.GetUtcNow()); expanded++; }
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear(); now = clock.GetUtcNow();
        var jobs = await db.FeedbackDeliveries.AsNoTracking().Where(d => (d.State == FeedbackDeliveryState.Pending || d.State == FeedbackDeliveryState.Retry || (d.State == FeedbackDeliveryState.Sending && d.LeaseUntilUtc <= now)) && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc).ThenBy(d => d.Id).Select(d => d.Id).Take(20).ToArrayAsync(ct);
        foreach (var id in jobs)
        {
            db.ChangeTracker.Clear(); var claim = Guid.NewGuid();
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                if (!await locks.LockAsync($"ClinIQ.FeedbackDelivery.{id:N}", ct)) continue;
                var row = await db.FeedbackDeliveries.SingleAsync(d => d.Id == id, ct);
                if (!row.Claim(claim, clock.GetUtcNow())) continue;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            attempted++; db.ChangeTracker.Clear();
            var job = await db.FeedbackDeliveries.AsNoTracking().SingleAsync(d => d.Id == id, ct);
            var notice = await db.FeedbackNotices.AsNoTracking().SingleAsync(n => n.Id == job.NoticeId, ct);
            var state = FeedbackDeliveryState.Cancelled; string? error = "Feedback.NotApplicable"; string? provider = null;
            try
            {
                if (await ApplicableAsync(notice.FeedbackRequestId, clock.GetUtcNow(), ct))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    NotificationDeliveryResult? result = null;
                    if (job.DeviceId.HasValue)
                    {
                        var device = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == job.DeviceId && d.UserId == job.RecipientUserId && d.SessionId == job.SessionId && d.BindingRevision == job.BindingRevision &&
                            db.AuthSessions.Any(s => s.Id == d.SessionId && s.RevokedAtUtc == null && s.ExpiresAtUtc > clock.GetUtcNow() && db.Users.Any(u => u.Id == s.UserId && u.EmailConfirmed && u.SecurityStamp == s.SecurityStamp) &&
                                db.UserRoles.Any(ur => ur.UserId == s.UserId && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "PATIENT"))), ct);
                        if (device?.ProtectedToken is not null) result = await transport.PushAsync(new(job.Id, notice.FeedbackRequestId, job.RecipientUserId, job.SessionId!.Value,
                            protection.CreateProtector("ClinIQ.PushToken.v1").Unprotect(device.ProtectedToken)), timeout.Token);
                        else error = "Push.BindingChanged";
                    }
                    else
                    {
                        var email = await db.Users.Where(u => u.Id == job.RecipientUserId && u.EmailConfirmed).Select(u => u.Email).SingleOrDefaultAsync(ct);
                        if (email is not null) result = await transport.EmailAsync(new(job.Id, notice.FeedbackRequestId, email), timeout.Token);
                        else error = "Email.RecipientUnavailable";
                    }
                    if (result is not null)
                    {
                        error = result.ErrorCode; provider = result.ProviderMessageId;
                        state = result.Outcome switch { DeliveryOutcome.Accepted => FeedbackDeliveryState.Accepted, DeliveryOutcome.DevelopmentFile => FeedbackDeliveryState.DevelopmentFile, DeliveryOutcome.Retry => FeedbackDeliveryState.Retry, _ => FeedbackDeliveryState.Cancelled };
                        if (result.Outcome == DeliveryOutcome.InvalidDevice)
                            await db.PushDevices.Where(d => d.Id == job.DeviceId && d.BindingRevision == job.BindingRevision && d.SessionId == job.SessionId)
                                .ExecuteUpdateAsync(s => s.SetProperty(d => d.UserId, (Guid?)null).SetProperty(d => d.SessionId, (Guid?)null).SetProperty(d => d.TokenHash, (string?)null).SetProperty(d => d.ProtectedToken, (string?)null)
                                    .SetProperty(d => d.BindingRevision, d => d.BindingRevision + 1).SetProperty(d => d.ChangedAtUtc, clock.GetUtcNow()), ct);
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { state = FeedbackDeliveryState.Retry; error = "Transport.Unavailable"; }
            var next = clock.GetUtcNow().Add(FeedbackDelivery.Backoff(job.Attempts));
            var completed = await db.FeedbackDeliveries.Where(d => d.Id == id && d.ClaimId == claim && d.State == FeedbackDeliveryState.Sending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, state).SetProperty(d => d.LastErrorCode, error).SetProperty(d => d.ProviderMessageId, provider)
                    .SetProperty(d => d.NextAttemptAtUtc, next).SetProperty(d => d.LeaseUntilUtc, (DateTimeOffset?)null), ct);
            if (completed == 0) { deferred++; continue; }
            if (state is FeedbackDeliveryState.Accepted or FeedbackDeliveryState.DevelopmentFile) accepted++;
            else if (state == FeedbackDeliveryState.Retry) deferred++; else cancelled++;
        }
        db.ChangeTracker.Clear(); return new(expanded, attempted, accepted, deferred, cancelled);
    }
    private Task<bool> ApplicableAsync(Guid id, DateTimeOffset now, CancellationToken ct) => db.AttendanceFeedbackRequests.AnyAsync(r => r.Id == id && r.Outcome == null && r.ReviewState == null && r.CreatedAtUtc <= now && r.ExpiresAtUtc > now &&
        db.Appointments.Any(a => a.Id == r.AppointmentId && a.Status == ClinIQ.Domain.Appointments.AppointmentStatus.NoShow && a.AttendanceRevision == r.SourceRevision), ct);
}
