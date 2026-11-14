using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Scheduling;

namespace ClinIQ.Application.Features.Scheduling;

public sealed record ScheduleRuleDto(Guid Id, Guid LocationId, ScheduleRuleData Data, string Version);
public sealed record SessionDto(Guid Id, Guid LocationId, Guid DoctorProfileId, Guid? SourceRuleId, string? SourceRuleVersion,
    DateOnly LocalDate, TimeOnly StartsLocal, TimeOnly EndsLocal, string TimeZoneId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    int MaxCapacity, int AllocatedCount, int BookingCutoffMinutes, DateTimeOffset BookingClosesAtUtc, bool BookingPaused, string Status,
    bool ResourcesAvailable, bool AcceptingNewBookings, bool ClosesAtOrBeforeStart, bool HasEnded, string Version,
    Guid? CancelledByUserId, DateTimeOffset? CancelledAtUtc, string? CancellationReason);
public sealed record ScheduleExceptionDto(Guid Id, Guid? LocationId, DateOnly LocalDate, string Reason, bool IsActive, string Version,
    string? RemovalReason, Guid? RemovedByUserId, DateTimeOffset? RemovedAtUtc);
public sealed record SchedulePage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record PublicationItem(Guid? SessionId, Guid SourceRuleId, string SourceRuleVersion, DateOnly LocalDate,
    DateTimeOffset? StartsAtUtc, DateTimeOffset? EndsAtUtc, DateTimeOffset? BookingClosesAtUtc, int MaxCapacity, int BookingCutoffMinutes,
    bool? ClosesAtOrBeforeStart, string Action);
public sealed record PublicationResult(IReadOnlyList<PublicationItem> Items, bool IsPreview, int Planned, int Created, int Preserved, int Suppressed, int SkippedPast);
public enum SessionEditKind { Times = 1, Capacity = 2, Cutoff = 3, Pause = 4, Resume = 5, Cancel = 6 }

public interface ISchedulingService
{
    Task<Result<ScheduleRuleDto>> CreateRuleAsync(Guid locationId, ScheduleRuleData input, CancellationToken ct);
    Task<Result<ScheduleRuleDto>> UpdateRuleAsync(Guid id, ScheduleRuleData input, string version, CancellationToken ct);
    Task<Result<ScheduleRuleDto>> GetRuleAsync(Guid id, CancellationToken ct);
    Task<Result<SchedulePage<ScheduleRuleDto>>> RulesAsync(Guid? locationId, int page, int pageSize, CancellationToken ct);
    Task<Result<PublicationResult>> PublishAsync(Guid locationId, DateOnly from, DateOnly until, bool preview, CancellationToken ct);
    Task<Result<SessionDto>> ExtraAsync(Guid locationId, DateOnly date, TimeOnly start, TimeOnly end, int capacity, int cutoff, CancellationToken ct);
    Task<Result<SessionDto>> EditSessionAsync(Guid id, SessionEditKind kind, string version, TimeOnly? start, TimeOnly? end, int? value, string? reason, CancellationToken ct);
    Task<Result<SessionDto>> GetSessionAsync(Guid id, CancellationToken ct);
    Task<Result<SchedulePage<SessionDto>>> SessionsAsync(Guid? locationId, DateOnly from, DateOnly until, int page, int pageSize, CancellationToken ct);
    Task<Result<ScheduleExceptionDto>> CreateExceptionAsync(Guid? locationId, DateOnly date, string reason, CancellationToken ct);
    Task<Result<ScheduleExceptionDto>> RemoveExceptionAsync(Guid id, string version, string reason, CancellationToken ct);
    Task<Result<ScheduleExceptionDto>> GetExceptionAsync(Guid id, CancellationToken ct);
    Task<Result<SchedulePage<ScheduleExceptionDto>>> ExceptionsAsync(DateOnly from, DateOnly until, int page, int pageSize, CancellationToken ct);
}
