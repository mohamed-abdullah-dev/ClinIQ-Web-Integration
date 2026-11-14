using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Attendance.Queries;

public sealed record AttendanceDoctorsQuery : IRequest<Result<IReadOnlyList<AttendanceDoctor>>>;
public sealed record AttendancePlacesQuery(Guid DoctorId) : IRequest<Result<IReadOnlyList<AttendancePlace>>>;
public sealed record AttendanceSessionsQuery(Guid DoctorId, Guid LocationId, DateOnly From, DateOnly Until, int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<AttendanceSession>>>;
public sealed record AttendanceQueueQuery(Guid DoctorId, Guid SessionId, int Page = 1, int PageSize = 20) : IRequest<Result<AttendanceQueue>>;
public sealed record AttendanceHistoryQuery(Guid DoctorId, Guid SessionId, Guid AppointmentId, int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<AttendanceHistory>>>;
public sealed class AttendancePlacesValidator : AbstractValidator<AttendancePlacesQuery>
{ public AttendancePlacesValidator() => RuleFor(x => x.DoctorId).NotEmpty(); }
public sealed class AttendanceSessionsValidator : AbstractValidator<AttendanceSessionsQuery>
{
    public AttendanceSessionsValidator()
    {
        RuleFor(x => x.DoctorId).NotEmpty(); RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x).Must(x => x.Until >= x.From && x.Until.DayNumber - x.From.DayNumber <= 30).WithMessage("Select an ascending date range of at most 31 days.");
        RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
public sealed class AttendanceQueueValidator : AbstractValidator<AttendanceQueueQuery>
{ public AttendanceQueueValidator() { RuleFor(x => x.DoctorId).NotEmpty(); RuleFor(x => x.SessionId).NotEmpty(); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class AttendanceHistoryValidator : AbstractValidator<AttendanceHistoryQuery>
{ public AttendanceHistoryValidator() { RuleFor(x => x.DoctorId).NotEmpty(); RuleFor(x => x.SessionId).NotEmpty(); RuleFor(x => x.AppointmentId).NotEmpty(); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class AttendanceDoctorsHandler(IAttendanceService s) : IRequestHandler<AttendanceDoctorsQuery, Result<IReadOnlyList<AttendanceDoctor>>>
{ public Task<Result<IReadOnlyList<AttendanceDoctor>>> Handle(AttendanceDoctorsQuery r, CancellationToken ct) => s.DoctorsAsync(ct); }
public sealed class AttendancePlacesHandler(IAttendanceService s) : IRequestHandler<AttendancePlacesQuery, Result<IReadOnlyList<AttendancePlace>>>
{ public Task<Result<IReadOnlyList<AttendancePlace>>> Handle(AttendancePlacesQuery r, CancellationToken ct) => s.PlacesAsync(r.DoctorId, ct); }
public sealed class AttendanceSessionsHandler(IAttendanceService s) : IRequestHandler<AttendanceSessionsQuery, Result<AttendancePage<AttendanceSession>>>
{ public Task<Result<AttendancePage<AttendanceSession>>> Handle(AttendanceSessionsQuery r, CancellationToken ct) => s.SessionsAsync(r.DoctorId, r.LocationId, r.From, r.Until, r.Page, r.PageSize, ct); }
public sealed class AttendanceQueueHandler(IAttendanceService s) : IRequestHandler<AttendanceQueueQuery, Result<AttendanceQueue>>
{ public Task<Result<AttendanceQueue>> Handle(AttendanceQueueQuery r, CancellationToken ct) => s.QueueAsync(r.DoctorId, r.SessionId, r.Page, r.PageSize, ct); }
public sealed class AttendanceHistoryHandler(IAttendanceService s) : IRequestHandler<AttendanceHistoryQuery, Result<AttendancePage<AttendanceHistory>>>
{ public Task<Result<AttendancePage<AttendanceHistory>>> Handle(AttendanceHistoryQuery r, CancellationToken ct) => s.HistoryAsync(r.DoctorId, r.SessionId, r.AppointmentId, r.Page, r.PageSize, ct); }
