using ClinIQ.Domain.Common.Results;

namespace ClinIQ.Application.Features.Subscriptions;

public sealed record ProTrialOfferDto(string Plan, int DurationDays, string TermsVersion);
public sealed record DoctorProTrialDto(DateTimeOffset ActivatedAtUtc, DateTimeOffset ExpiresAtUtc, string TermsVersion);
public sealed record DoctorSubscriptionDto(string EffectivePlan, bool CanActivateProTrial, ProTrialOfferDto Offer, DoctorProTrialDto? Trial);

public interface IDoctorSubscriptionService
{
    Task<Result<DoctorSubscriptionDto>> GetOwnAsync(CancellationToken cancellationToken);
    Task<Result<DoctorSubscriptionDto>> ActivateOwnProTrialAsync(CancellationToken cancellationToken);
}

public static class DoctorSubscriptionErrors
{
    public static readonly Error Unauthorized = Error.Unauthorized("DoctorSubscription.Unauthorized", "Sign in before viewing or activating a doctor subscription.");
    public static readonly Error Forbidden = Error.Forbidden("DoctorSubscription.Forbidden", "A confirmed and approved Doctor Web account is required.");
    public static readonly Error Busy = Error.Conflict("DoctorSubscription.Busy", "Another activation is in progress. Try again.");
    public static readonly Error TrialUsed = Error.Conflict("DoctorSubscription.TrialUsed", "The Pro trial has already been used for this doctor.");
}
