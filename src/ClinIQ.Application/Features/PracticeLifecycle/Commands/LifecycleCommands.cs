using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLifecycle;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.PracticeLifecycle.Commands;

public static class LifecycleValidation
{
    public static bool Version(string? value)
    {
        try { return value is not null && value.Length <= 24 && Convert.FromBase64String(value).Length == 8; }
        catch (FormatException) { return false; }
    }
    public static bool Hash(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool Page(int page, int size) => page is >= 1 and <= 100000 && size is >= 1 and <= 100;
}
public sealed record ChangeLifecycleCommand(OperationalScopeKind Kind, Guid Id, bool Finalize, LifecycleChangeInput Input) : IRequest<Result<LifecycleScopeDto>>;
public sealed record RequestEmergencyCommand(OperationalScopeKind Kind, Guid Id, EmergencyRequestInput Input) : IRequest<Result<EmergencySummary>>;
public sealed record DecideEmergencyCommand(Guid Id, EmergencyDecisionInput Input) : IRequest<Result<EmergencySummary>>;

public sealed class LifecycleChangeInputValidator : AbstractValidator<LifecycleChangeInput>
{
    public LifecycleChangeInputValidator()
    { RuleFor(x => x.Version).Must(LifecycleValidation.Version); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000); }
}
public sealed class EmergencyRequestInputValidator : AbstractValidator<EmergencyRequestInput>
{
    public EmergencyRequestInputValidator()
    {
        RuleFor(x => x.Version).Must(LifecycleValidation.Version);
        RuleFor(x => x.Category).IsInEnum(); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
    }
}
public sealed class EmergencyDecisionInputValidator : AbstractValidator<EmergencyDecisionInput>
{
    public EmergencyDecisionInputValidator()
    {
        RuleFor(x => x.Approve).NotNull();
        RuleFor(x => x.Version).Must(LifecycleValidation.Version); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ScopeVersion).Must(LifecycleValidation.Version).When(x => x.Approve == true);
        RuleFor(x => x.PreviewHash).Must(LifecycleValidation.Hash).When(x => x.Approve == true);
    }
}
public sealed class ChangeLifecycleValidator : AbstractValidator<ChangeLifecycleCommand>
{
    public ChangeLifecycleValidator()
    { RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new LifecycleChangeInputValidator()); }
}
public sealed class RequestEmergencyValidator : AbstractValidator<RequestEmergencyCommand>
{
    public RequestEmergencyValidator()
    { RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new EmergencyRequestInputValidator()); }
}
public sealed class DecideEmergencyValidator : AbstractValidator<DecideEmergencyCommand>
{
    public DecideEmergencyValidator()
    { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new EmergencyDecisionInputValidator()); }
}
public sealed class ChangeLifecycleHandler(IPracticeLifecycleService service) : IRequestHandler<ChangeLifecycleCommand, Result<LifecycleScopeDto>>
{ public Task<Result<LifecycleScopeDto>> Handle(ChangeLifecycleCommand r, CancellationToken ct) => service.ChangeAsync(r.Kind, r.Id, r.Finalize, r.Input, ct); }
public sealed class RequestEmergencyHandler(IPracticeLifecycleService service) : IRequestHandler<RequestEmergencyCommand, Result<EmergencySummary>>
{ public Task<Result<EmergencySummary>> Handle(RequestEmergencyCommand r, CancellationToken ct) => service.RequestAsync(r.Kind, r.Id, r.Input, ct); }
public sealed class DecideEmergencyHandler(IPracticeLifecycleService service) : IRequestHandler<DecideEmergencyCommand, Result<EmergencySummary>>
{ public Task<Result<EmergencySummary>> Handle(DecideEmergencyCommand r, CancellationToken ct) => service.DecideAsync(r.Id, r.Input, ct); }
