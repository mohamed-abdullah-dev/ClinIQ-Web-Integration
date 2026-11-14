using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Notifications;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
namespace ClinIQ.Infrastructure.Notifications;
internal sealed class NotificationAdministrationService(AppDbContext db, ICurrentUser current, IHttpContextAccessor http, IFeedbackDispatcher dispatcher) : INotificationAdministrationService
{
    private Task<bool> Allowed(CancellationToken ct) => current.UserId is Guid id && http.HttpContext?.User.FindFirst("client")?.Value == "Web"
        ? db.Users.AnyAsync(u => u.Id == id && u.EmailConfirmed && db.UserRoles.Any(ur => ur.UserId == id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "ADMIN")), ct) : Task.FromResult(false);
    public async Task<Result<FeedbackDispatchResult>> RunAsync(CancellationToken ct) => await Allowed(ct) ? await dispatcher.RunAsync(ct) : NotificationErrors.Forbidden;
    public async Task<Result<IReadOnlyList<FeedbackDeliveryDto>>> ListAsync(int page, int size, CancellationToken ct)
    {
        if (!await Allowed(ct)) return NotificationErrors.Forbidden;
        var rows = await (from d in db.FeedbackDeliveries.AsNoTracking() join n in db.FeedbackNotices on d.NoticeId equals n.Id orderby d.NextAttemptAtUtc, d.Id
                          select new FeedbackDeliveryDto(d.Id, n.FeedbackRequestId, n.Kind, d.State, d.Attempts, d.NextAttemptAtUtc, d.LastAttemptAtUtc, d.LastErrorCode)).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return Result<IReadOnlyList<FeedbackDeliveryDto>>.Success(rows);
    }
}
