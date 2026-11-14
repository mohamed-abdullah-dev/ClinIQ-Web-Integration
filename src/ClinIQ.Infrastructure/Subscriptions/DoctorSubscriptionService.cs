using System.Data;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Subscriptions;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Subscriptions;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Subscriptions;

internal sealed class DoctorSubscriptionService(AppDbContext db, ICurrentUser currentUser,
    IHttpContextAccessor http, WorkforceAccess access, TimeProvider clock) : IDoctorSubscriptionService
{
    private static readonly ProTrialOfferDto Offer = new(nameof(DoctorPlan.Pro), DoctorProTrial.DurationDays, DoctorProTrial.CurrentTermsVersion);

    public async Task<Result<DoctorSubscriptionDto>> GetOwnAsync(CancellationToken cancellationToken)
    {
        var actor = WebActor();
        if (actor.IsError) return Result<DoctorSubscriptionDto>.Failure(actor.Errors);

        var current = await access.Doctors.AsNoTracking().Where(p => p.UserId == actor.Value)
            .Select(p => new { Trial = db.DoctorProTrials.AsNoTracking().SingleOrDefault(t => t.Id == p.Id) })
            .SingleOrDefaultAsync(cancellationToken);
        return current is null ? DoctorSubscriptionErrors.Forbidden : ToDto(current.Trial, clock.GetUtcNow());
    }

    public async Task<Result<DoctorSubscriptionDto>> ActivateOwnProTrialAsync(CancellationToken cancellationToken)
    {
        var actor = WebActor();
        if (actor.IsError) return Result<DoctorSubscriptionDto>.Failure(actor.Errors);

        // Keep the eligibility read valid through commit. The application lock serializes requests from this account.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await access.LockAsync($"ClinIQ.DoctorProTrial.{actor.Value:N}", cancellationToken)) return DoctorSubscriptionErrors.Busy;

        var profileId = await access.Doctors.Where(p => p.UserId == actor.Value)
            .Select(p => (Guid?)p.Id).SingleOrDefaultAsync(cancellationToken);
        if (profileId is null) return DoctorSubscriptionErrors.Forbidden;

        var existing = await db.DoctorProTrials.AsNoTracking().SingleOrDefaultAsync(t => t.Id == profileId.Value, cancellationToken);
        var now = clock.GetUtcNow();
        if (existing is not null)
        {
            if (now >= existing.ExpiresAtUtc) return DoctorSubscriptionErrors.TrialUsed;
            return ToDto(existing, now);
        }

        var trial = DoctorProTrial.Activate(profileId.Value, now);
        db.DoctorProTrials.Add(trial);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToDto(trial, now);
    }

    private Result<Guid> WebActor()
    {
        if (currentUser.UserId is not Guid id) return DoctorSubscriptionErrors.Unauthorized;
        if (http.HttpContext?.User.FindFirst("client")?.Value != "Web") return DoctorSubscriptionErrors.Forbidden;
        return id;
    }

    private static DoctorSubscriptionDto ToDto(DoctorProTrial? trial, DateTimeOffset now) => new(
        (trial?.EffectivePlanAt(now) ?? DoctorPlan.Standard).ToString(),
        trial is null,
        Offer,
        trial is null ? null : new DoctorProTrialDto(trial.ActivatedAtUtc, trial.ExpiresAtUtc, trial.TermsVersion));
}
