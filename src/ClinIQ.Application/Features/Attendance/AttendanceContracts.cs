using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;

namespace ClinIQ.Application.Features.Attendance;

public sealed record AttendancePage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record AttendanceDoctor(Guid Id, string Name);
public sealed record AttendancePlace(Guid Id, Guid? OperationalBranchId, string Name, string Address);
public sealed record AttendanceSession(Guid Id, Guid DoctorProfileId, Guid LocationId, Guid? OperationalBranchId, DateOnly LocalDate,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, string TimeZoneId, string Status, int MaxCapacity, int AllocatedCount, int LastQueueNumber);
public sealed record AttendanceAppointment(Guid Id, Guid PatientId, string ContactName, string ContactPhone, decimal AgreedFee, string Currency, AttendanceFacts Facts, string Version);
public sealed record AttendanceQueue(AttendanceSession Session, AttendancePage<AttendanceAppointment> Appointments);
public sealed record AttendanceWriteResult(AttendanceAppointment Appointment, bool Changed);
public sealed record AttendanceHistory(Guid Id, int Revision, Guid ActorUserId, DateTimeOffset RecordedAtUtc, string Action, string Reason, AttendanceFacts Before, AttendanceFacts After);
public interface IAttendanceService
{
    Task<Result<IReadOnlyList<AttendanceDoctor>>> DoctorsAsync(CancellationToken ct);
    Task<Result<IReadOnlyList<AttendancePlace>>> PlacesAsync(Guid doctor, CancellationToken ct);
    Task<Result<AttendancePage<AttendanceSession>>> SessionsAsync(Guid doctor, Guid location, DateOnly from, DateOnly until, int page, int size, CancellationToken ct);
    Task<Result<AttendanceQueue>> QueueAsync(Guid doctor, Guid session, int page, int size, CancellationToken ct);
    Task<Result<AttendancePage<AttendanceHistory>>> HistoryAsync(Guid doctor, Guid session, Guid appointment, int page, int size, CancellationToken ct);
    Task<Result<AttendanceWriteResult>> WriteAsync(Guid doctor, Guid session, Guid appointment, AttendanceInput input, CancellationToken ct);
}
