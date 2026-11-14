using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLifecycle;
using ClinIQ.Domain.Appointments;

namespace ClinIQ.Application.Features.PracticeLifecycle;

public sealed record LifecycleChangeInput(string Version, string Reason);
public sealed record EmergencyRequestInput(string Version, EmergencyReasonCategory Category, string Reason);
public sealed record EmergencyDecisionInput(bool? Approve, string Version, string Reason, string? ScopeVersion = null, string? PreviewHash = null);
public sealed record LifecycleScopeDto(OperationalScopeKind Kind, Guid Id, string Name, string State, bool IsActive,
    Guid? ParentBranchId, string? ParentBranchState, bool AcceptingNewBookings, int ConfirmedCount, int CheckedInCount,
    bool CanFinalize, string Version);
public sealed record LifecyclePage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record LifecycleHistoryDto(Guid Id, Guid ActorUserId, string Action, string Reason, int BeforeState,
    int AfterState, DateTimeOffset RecordedAtUtc, string SourceVersion);
public sealed record EmergencySummary(Guid Id, Guid? LocationId, Guid? BranchId, Guid RequesterUserId,
    EmergencyReasonCategory Category, string Reason, EmergencyRequestStatus Status, DateTimeOffset CreatedAtUtc,
    Guid? ReviewerUserId, string? ReviewReason, DateTimeOffset? ReviewedAtUtc, int? CancelledCount,
    string? ReviewedPreviewHash, string? ReviewedScopeVersion, string Version);
public sealed record EmergencyCancellationDto(Guid AppointmentId, string SourceVersion, string BeforeJson, string AfterJson, DateTimeOffset RecordedAtUtc);
public sealed record AffectedVisit(Guid Id, Guid SessionId, Guid DoctorProfileId, Guid LocationId,
    AppointmentStatus Status, DateTimeOffset StartsAtUtc, string Version);
public sealed record EmergencyDetail(EmergencySummary Request, LifecycleScopeDto Scope, string PreviewHash,
    LifecyclePage<AffectedVisit> AffectedVisits);

public interface IPracticeLifecycleService
{
    Task<Result<LifecycleScopeDto>> GetAsync(OperationalScopeKind kind, Guid id, CancellationToken ct);
    Task<Result<LifecycleScopeDto>> ChangeAsync(OperationalScopeKind kind, Guid id, bool finalize, LifecycleChangeInput input, CancellationToken ct);
    Task<Result<LifecyclePage<LifecycleHistoryDto>>> HistoryAsync(OperationalScopeKind kind, Guid id, int page, int size, CancellationToken ct);
    Task<Result<EmergencySummary>> RequestAsync(OperationalScopeKind kind, Guid id, EmergencyRequestInput input, CancellationToken ct);
    Task<Result<LifecyclePage<EmergencySummary>>> RequestsAsync(bool admin, OperationalScopeKind? kind, Guid? scopeId,
        EmergencyRequestStatus? status, int page, int size, CancellationToken ct);
    Task<Result<EmergencyDetail>> DetailAsync(Guid id, bool admin, int page, int size, CancellationToken ct);
    Task<Result<EmergencySummary>> DecideAsync(Guid id, EmergencyDecisionInput input, CancellationToken ct);
    Task<Result<LifecyclePage<EmergencyCancellationDto>>> CancellationsAsync(Guid id, int page, int size, CancellationToken ct);
}
