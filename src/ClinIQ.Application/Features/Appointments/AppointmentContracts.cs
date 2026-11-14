using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Application.Features.Scheduling;

namespace ClinIQ.Application.Features.Appointments;

public sealed record AppointmentDto(Guid Id, AppointmentSelection Selection, string Status, Guid? ReplacesAppointmentId,
    Guid? ReplacedByAppointmentId, DateTimeOffset CreatedAtUtc, string? CancellationReason, string? CancellationCategory, string? PriorStatus, string Version,
    int? QueueNumber = null, DateTimeOffset? ArrivedAtUtc = null, DateTimeOffset? CompletedAtUtc = null);
public sealed record AppointmentPage(IReadOnlyList<AppointmentDto> Items, int TotalCount, int Page, int PageSize);
public sealed record OverlappingAppointment(Guid Id, Guid DoctorProfileId, string DoctorName, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);
public sealed record BookingPreview(AppointmentSelection Selection, IReadOnlyList<OverlappingAppointment> Overlaps,
    string? Warning, string QuoteToken, DateTimeOffset ExpiresAtUtc);
public sealed record BookingConfirmation(Guid SessionId, string QuoteToken, Guid RequestId, bool AcceptPrice, bool ConfirmContact, bool AcknowledgeOverlap);
public sealed record AppointmentOperationResult(AppointmentDto Appointment, bool Replayed);
public sealed record BookingNoticeRetryResult(int Attempted, int DevelopmentFiles, int Unavailable);
public interface IAppointmentService
{
    Task<Result<BookingPreview>> PreviewAsync(Guid sessionId, Guid? sourceId, CancellationToken ct);
    Task<Result<AppointmentOperationResult>> BookAsync(BookingConfirmation input, CancellationToken ct);
    Task<Result<AppointmentOperationResult>> CancelAsync(Guid id, Guid requestId, string reason, CancellationToken ct);
    Task<Result<AppointmentOperationResult>> RescheduleAsync(Guid id, BookingConfirmation input, CancellationToken ct);
    Task<Result<AppointmentDto>> GetAsync(Guid id, CancellationToken ct);
    Task<Result<AppointmentPage>> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<Result<SessionDto>> CancelSessionAsync(Guid id, string version, string reason, CancellationToken ct);
    Task<Result<BookingNoticeRetryResult>> RetryNoticesAsync(CancellationToken ct);
}
