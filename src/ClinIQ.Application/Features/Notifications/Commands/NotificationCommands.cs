using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Notifications.Commands;

public sealed record RegisterPushDeviceCommand(RegisterPushDeviceInput Input) : IRequest<Result<PushDeviceDto>>;
public sealed record DetachPushDeviceCommand(Guid Id) : IRequest<Result<Success>>;
public sealed record ProcessFeedbackNotificationsCommand : IRequest<Result<FeedbackDispatchResult>>;
public sealed class PushDeviceInputValidator : AbstractValidator<RegisterPushDeviceInput>
{
    public PushDeviceInputValidator()
    {
        RuleFor(x => x.InstallationId).NotEmpty(); RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.InstallationSecret).Must(s => s is { Length: 43 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        RuleFor(x => x.Token).Must(t => t is { Length: > 0 and <= 4096 } && t.All(c => c >= 33 && c <= 126));
    }
}
public sealed class RegisterPushDeviceValidator : AbstractValidator<RegisterPushDeviceCommand>
{ public RegisterPushDeviceValidator() => RuleFor(x => x.Input).NotNull().SetValidator(new PushDeviceInputValidator()); }
public sealed class DetachPushDeviceValidator : AbstractValidator<DetachPushDeviceCommand>
{ public DetachPushDeviceValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class RegisterPushDeviceHandler(IPushDeviceService s) : IRequestHandler<RegisterPushDeviceCommand, Result<PushDeviceDto>>
{ public Task<Result<PushDeviceDto>> Handle(RegisterPushDeviceCommand r, CancellationToken ct) => s.RegisterAsync(r.Input, ct); }
public sealed class DetachPushDeviceHandler(IPushDeviceService s) : IRequestHandler<DetachPushDeviceCommand, Result<Success>>
{ public Task<Result<Success>> Handle(DetachPushDeviceCommand r, CancellationToken ct) => s.DetachAsync(r.Id, ct); }
public sealed class ProcessFeedbackNotificationsHandler(INotificationAdministrationService s) : IRequestHandler<ProcessFeedbackNotificationsCommand, Result<FeedbackDispatchResult>>
{ public Task<Result<FeedbackDispatchResult>> Handle(ProcessFeedbackNotificationsCommand r, CancellationToken ct) => s.RunAsync(ct); }
