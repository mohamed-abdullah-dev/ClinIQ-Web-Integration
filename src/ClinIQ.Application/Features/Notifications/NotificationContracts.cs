using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Notifications;

namespace ClinIQ.Application.Features.Notifications;

public sealed record RegisterPushDeviceInput(Guid InstallationId, string InstallationSecret, string Token, PushPlatform Platform);
public sealed record PushDeviceDto(Guid InstallationId, Guid SessionId, PushPlatform Platform, int BindingRevision);
public interface IPushDeviceService
{
    Task<Result<PushDeviceDto>> RegisterAsync(RegisterPushDeviceInput input, CancellationToken ct);
    Task<Result<Success>> DetachAsync(Guid id, CancellationToken ct);
    Task<Result<IReadOnlyList<PushDeviceDto>>> ListAsync(CancellationToken ct);
}
public sealed record FeedbackPushMessage(Guid DeliveryId, Guid FeedbackRequestId, Guid RecipientUserId, Guid SessionId, string Token);
public sealed record FeedbackEmailMessage(Guid DeliveryId, Guid FeedbackRequestId, string Email);
public enum DeliveryOutcome { Accepted, DevelopmentFile, Retry, InvalidDevice }
public sealed record NotificationDeliveryResult(DeliveryOutcome Outcome, string? ErrorCode = null, string? ProviderMessageId = null);
public interface IFeedbackNotificationTransport
{
    Task<NotificationDeliveryResult> PushAsync(FeedbackPushMessage message, CancellationToken ct);
    Task<NotificationDeliveryResult> EmailAsync(FeedbackEmailMessage message, CancellationToken ct);
}
public sealed record FeedbackDispatchResult(int Expanded, int Attempted, int Accepted, int Deferred, int Cancelled);
public interface IFeedbackDispatcher { Task<FeedbackDispatchResult> RunAsync(CancellationToken ct); }
public sealed record FeedbackDeliveryDto(Guid Id, Guid FeedbackRequestId, FeedbackNoticeKind Kind, FeedbackDeliveryState State,
    int Attempts, DateTimeOffset NextAttemptAtUtc, DateTimeOffset? LastAttemptAtUtc, string? LastErrorCode);
public interface INotificationAdministrationService
{
    Task<Result<FeedbackDispatchResult>> RunAsync(CancellationToken ct);
    Task<Result<IReadOnlyList<FeedbackDeliveryDto>>> ListAsync(int page, int size, CancellationToken ct);
}
public static class NotificationErrors
{
    public static readonly Error Unauthorized = Error.Unauthorized("Notifications.Unauthorized", "A current authenticated session is required.");
    public static readonly Error Forbidden = Error.Forbidden("Notifications.Forbidden", "This role/client cannot perform the requested notification operation.");
    public static readonly Error Invalid = Error.Validation("Notifications.InvalidDevice", "Provide an installation ID, a 43-character cryptographically random base64url installation secret, a nonempty ASCII token up to 4096 characters, and a supported platform.");
    public static readonly Error Proof = Error.Conflict("Notifications.InstallationProof", "This installation cannot be reassigned without its existing installation secret.");
    public static readonly Error TokenInUse = Error.Conflict("Notifications.TokenInUse", "The delivery token is already bound to a different installation. Detach that installation or renew its token.");
    public static readonly Error NotFound = Error.NotFound("Notifications.NotFound", "The device is outside the current session's scope.");
    public static readonly Error Busy = Error.Conflict("Notifications.Busy", "The session or device is being changed. Retry the same request.");
}
