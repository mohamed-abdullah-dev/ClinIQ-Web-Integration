using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.Notifications;

public enum FeedbackDeliveryState { Pending = 1, Sending = 2, Accepted = 3, DevelopmentFile = 4, Retry = 5, Cancelled = 6 }
public sealed class FeedbackDelivery : Entity
{
    public Guid NoticeId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public Guid? DeviceId { get; private set; }
    public Guid? SessionId { get; private set; }
    public int? BindingRevision { get; private set; }
    public string TargetKey { get; private set; } = null!;
    public FeedbackDeliveryState State { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public DateTimeOffset? LeaseUntilUtc { get; private set; }
    public Guid? ClaimId { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? ProviderMessageId { get; private set; }
    private FeedbackDelivery() { }
    private FeedbackDelivery(Guid id) : base(id) { }
    public static FeedbackDelivery Create(FeedbackNotice notice, Guid user, PushDevice? device = null)
    {
        if (user == Guid.Empty || (notice.Kind == FeedbackNoticeKind.InitialPush ? device is null || device.UserId != user || device.SessionId is null : device is not null)) throw new ArgumentException("Invalid notice recipient.");
        return new(Guid.NewGuid())
        {
            NoticeId = notice.Id, RecipientUserId = user, DeviceId = device?.Id, SessionId = device?.SessionId,
            BindingRevision = device?.BindingRevision, TargetKey = device?.Id.ToString("N") ?? "email",
            State = FeedbackDeliveryState.Pending, NextAttemptAtUtc = notice.DueAtUtc
        };
    }
    public bool Claim(Guid claim, DateTimeOffset now)
    {
        if (State is FeedbackDeliveryState.Accepted or FeedbackDeliveryState.DevelopmentFile or FeedbackDeliveryState.Cancelled ||
            now < NextAttemptAtUtc || (State == FeedbackDeliveryState.Sending && now < LeaseUntilUtc)) return false;
        State = FeedbackDeliveryState.Sending; ClaimId = claim; Attempts = checked(Attempts + 1);
        LastAttemptAtUtc = now; LeaseUntilUtc = now.AddMinutes(2); return true;
    }
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(3600, 60 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));
}
