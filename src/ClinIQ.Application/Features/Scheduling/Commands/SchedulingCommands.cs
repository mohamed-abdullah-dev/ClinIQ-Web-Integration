using ClinIQ.Application.Features.PracticeLocations.Commands;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Scheduling;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Scheduling.Commands;

public sealed record CreateScheduleRuleCommand(Guid LocationId, ScheduleRuleData Input) : IRequest<Result<ScheduleRuleDto>>;
public sealed record UpdateScheduleRuleCommand(Guid Id, ScheduleRuleData Input, string Version) : IRequest<Result<ScheduleRuleDto>>;
public sealed record PublishScheduleCommand(Guid LocationId, DateOnly From, DateOnly Until, bool Preview) : IRequest<Result<PublicationResult>>;
public sealed record ExtraSessionCommand(Guid LocationId, DateOnly Date, TimeOnly Start, TimeOnly End, int MaxCapacity, int BookingCutoffMinutes) : IRequest<Result<SessionDto>>;
public sealed record EditSessionCommand(Guid Id, SessionEditKind Kind, string Version, TimeOnly? Start = null, TimeOnly? End = null, int? Value = null, string? Reason = null) : IRequest<Result<SessionDto>>;
public sealed record CreateScheduleExceptionCommand(Guid? LocationId, DateOnly Date, string Reason) : IRequest<Result<ScheduleExceptionDto>>;
public sealed record RemoveScheduleExceptionCommand(Guid Id, string Version, string Reason) : IRequest<Result<ScheduleExceptionDto>>;
public sealed class CreateScheduleRuleValidator : AbstractValidator<CreateScheduleRuleCommand>
{
    public CreateScheduleRuleValidator() { RuleFor(x => x.LocationId).NotEmpty(); RuleFor(x => x.Input).NotNull(); RuleFor(x => x).Must(x => x.Input is not null && x.Input.Validate().IsSuccess).WithMessage("Provide valid same-day weekly schedule settings."); }
}
public sealed class UpdateScheduleRuleValidator : AbstractValidator<UpdateScheduleRuleCommand>
{
    public UpdateScheduleRuleValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Version).Must(PracticeLocationValidation.IsVersion); RuleFor(x => x.Input).NotNull(); RuleFor(x => x).Must(x => x.Input is not null && x.Input.Validate().IsSuccess).WithMessage("Provide valid same-day weekly schedule settings."); }
}
public sealed class PublishScheduleValidator : AbstractValidator<PublishScheduleCommand>
{ public PublishScheduleValidator() { RuleFor(x => x.LocationId).NotEmpty(); RuleFor(x => x.Until).GreaterThanOrEqualTo(x => x.From); } }
public sealed class ExtraSessionValidator : AbstractValidator<ExtraSessionCommand>
{
    public ExtraSessionValidator() { RuleFor(x => x.LocationId).NotEmpty(); RuleFor(x => x.MaxCapacity).GreaterThan(0); RuleFor(x => x.BookingCutoffMinutes).GreaterThanOrEqualTo(0); RuleFor(x => x).Must(x => x.End > x.Start && x.Start.Ticks % TimeSpan.TicksPerMinute == 0 && x.End.Ticks % TimeSpan.TicksPerMinute == 0); }
}
public sealed class EditSessionValidator : AbstractValidator<EditSessionCommand>
{
    public EditSessionValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Version).Must(PracticeLocationValidation.IsVersion);
        RuleFor(x => x).Must(x => x.Start.HasValue && x.End.HasValue && x.End > x.Start && x.Start.Value.Ticks % TimeSpan.TicksPerMinute == 0 && x.End.Value.Ticks % TimeSpan.TicksPerMinute == 0).When(x => x.Kind == SessionEditKind.Times);
        RuleFor(x => x.Value).NotNull().GreaterThan(0).When(x => x.Kind == SessionEditKind.Capacity);
        RuleFor(x => x.Value).NotNull().GreaterThanOrEqualTo(0).When(x => x.Kind == SessionEditKind.Cutoff);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000).When(x => x.Kind == SessionEditKind.Cancel);
    }
}
public sealed class CreateScheduleExceptionValidator : AbstractValidator<CreateScheduleExceptionCommand>
{ public CreateScheduleExceptionValidator() { RuleFor(x => x.LocationId).NotEqual(Guid.Empty).When(x => x.LocationId.HasValue); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000); } }
public sealed class RemoveScheduleExceptionValidator : AbstractValidator<RemoveScheduleExceptionCommand>
{ public RemoveScheduleExceptionValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Version).Must(PracticeLocationValidation.IsVersion); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000); } }
public sealed class CreateScheduleRuleHandler(ISchedulingService s) : IRequestHandler<CreateScheduleRuleCommand, Result<ScheduleRuleDto>>
{ public Task<Result<ScheduleRuleDto>> Handle(CreateScheduleRuleCommand r, CancellationToken ct) => s.CreateRuleAsync(r.LocationId, r.Input, ct); }
public sealed class UpdateScheduleRuleHandler(ISchedulingService s) : IRequestHandler<UpdateScheduleRuleCommand, Result<ScheduleRuleDto>>
{ public Task<Result<ScheduleRuleDto>> Handle(UpdateScheduleRuleCommand r, CancellationToken ct) => s.UpdateRuleAsync(r.Id, r.Input, r.Version, ct); }
public sealed class PublishScheduleHandler(ISchedulingService s) : IRequestHandler<PublishScheduleCommand, Result<PublicationResult>>
{ public Task<Result<PublicationResult>> Handle(PublishScheduleCommand r, CancellationToken ct) => s.PublishAsync(r.LocationId, r.From, r.Until, r.Preview, ct); }
public sealed class ExtraSessionHandler(ISchedulingService s) : IRequestHandler<ExtraSessionCommand, Result<SessionDto>>
{ public Task<Result<SessionDto>> Handle(ExtraSessionCommand r, CancellationToken ct) => s.ExtraAsync(r.LocationId, r.Date, r.Start, r.End, r.MaxCapacity, r.BookingCutoffMinutes, ct); }
public sealed class EditSessionHandler(ISchedulingService s) : IRequestHandler<EditSessionCommand, Result<SessionDto>>
{ public Task<Result<SessionDto>> Handle(EditSessionCommand r, CancellationToken ct) => s.EditSessionAsync(r.Id, r.Kind, r.Version, r.Start, r.End, r.Value, r.Reason, ct); }
public sealed class CreateScheduleExceptionHandler(ISchedulingService s) : IRequestHandler<CreateScheduleExceptionCommand, Result<ScheduleExceptionDto>>
{ public Task<Result<ScheduleExceptionDto>> Handle(CreateScheduleExceptionCommand r, CancellationToken ct) => s.CreateExceptionAsync(r.LocationId, r.Date, r.Reason, ct); }
public sealed class RemoveScheduleExceptionHandler(ISchedulingService s) : IRequestHandler<RemoveScheduleExceptionCommand, Result<ScheduleExceptionDto>>
{ public Task<Result<ScheduleExceptionDto>> Handle(RemoveScheduleExceptionCommand r, CancellationToken ct) => s.RemoveExceptionAsync(r.Id, r.Version, r.Reason, ct); }
