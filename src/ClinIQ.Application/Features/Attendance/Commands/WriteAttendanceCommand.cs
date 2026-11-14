using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Attendance.Commands;

public sealed record WriteAttendanceCommand(Guid DoctorId, Guid SessionId, Guid AppointmentId, AttendanceInput Input) : IRequest<Result<AttendanceWriteResult>>;
public sealed class WriteAttendanceValidator : AbstractValidator<WriteAttendanceCommand>
{
    public WriteAttendanceValidator()
    {
        RuleFor(x => x.DoctorId).NotEmpty(); RuleFor(x => x.SessionId).NotEmpty(); RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.Input).NotNull().SetValidator(new AttendanceInputValidator());
    }
}
public sealed class AttendanceInputValidator : AbstractValidator<AttendanceInput>
{
    public AttendanceInputValidator()
    {
        RuleFor(x => x.ConfirmedPatientId).NotEmpty(); RuleFor(x => x.Action).IsInEnum();
        RuleFor(x => x.Version).NotEmpty().MaximumLength(24).Must(x => { try { return Convert.FromBase64String(x ?? "").Length == 8; } catch (FormatException) { return false; } });
        RuleFor(x => x.Reason).MaximumLength(2000);
        RuleFor(x => x.Reason).NotEmpty().When(x => x.Action is AttendanceAction.Correct or AttendanceAction.NotSeen or AttendanceAction.LateCheckIn);
        RuleFor(x => x.CompletedAtUtc).NotNull().When(x => x.Action == AttendanceAction.Complete || (x.Action == AttendanceAction.Correct && x.CorrectedStatus == AppointmentStatus.Completed));
        RuleFor(x => x.ArrivedAtUtc).NotNull().When(x => x.Action == AttendanceAction.LateCheckIn);
        RuleFor(x => x.CorrectedStatus).Must(x => x is AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn or AppointmentStatus.Completed or AppointmentStatus.NoShow or AppointmentStatus.AttendedNotSeen).When(x => x.Action == AttendanceAction.Correct);
    }
}
public sealed class WriteAttendanceHandler(IAttendanceService s) : IRequestHandler<WriteAttendanceCommand, Result<AttendanceWriteResult>>
{ public Task<Result<AttendanceWriteResult>> Handle(WriteAttendanceCommand r, CancellationToken ct) => s.WriteAsync(r.DoctorId, r.SessionId, r.AppointmentId, r.Input, ct); }
