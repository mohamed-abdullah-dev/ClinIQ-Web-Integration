using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.Subscriptions;

public enum DoctorPlan { Standard, Pro }

public sealed class DoctorProTrial : Entity
{
    public const int DurationDays = 30;
    public const string CurrentTermsVersion = "doctor-pro-trial-v1";

    public DateTimeOffset ActivatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public string TermsVersion { get; private set; } = null!;

    private DoctorProTrial() { }

    // A trial has the same key as the doctor profile it belongs to.
    private DoctorProTrial(Guid doctorProfileId, DateTimeOffset activatedAt) : base(doctorProfileId)
    {
        ActivatedAtUtc = activatedAt.ToUniversalTime();
        ExpiresAtUtc = ActivatedAtUtc.AddDays(DurationDays);
        TermsVersion = CurrentTermsVersion;
    }

    public static DoctorProTrial Activate(Guid doctorProfileId, DateTimeOffset now) => new(doctorProfileId, now);

    public DoctorPlan EffectivePlanAt(DateTimeOffset now) => now >= ActivatedAtUtc && now < ExpiresAtUtc
        ? DoctorPlan.Pro
        : DoctorPlan.Standard;
}
