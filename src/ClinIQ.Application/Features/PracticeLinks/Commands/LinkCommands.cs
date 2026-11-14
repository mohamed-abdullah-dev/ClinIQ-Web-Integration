using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.PracticeLinks.Commands;

public sealed record CreateLinkCommand(LinkCreateInput Input) : IRequest<Result<LinkRequestDto>>;
public sealed record DecideLinkCommand(Guid Id, LinkDecisionInput Input) : IRequest<Result<LinkRequestDto>>;
public sealed record WithdrawLinkCommand(Guid Id, LinkWithdrawInput Input) : IRequest<Result<LinkRequestDto>>;
public sealed class LinkCreateValidator : AbstractValidator<LinkCreateInput>
{
    public LinkCreateValidator()
    {
        RuleFor(x => x.LocationId).NotEmpty(); RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.LocationVersion).Must(LifecycleValidation.Version); RuleFor(x => x.BranchVersion).Must(LifecycleValidation.Version);
        RuleFor(x => x.SamePremises).Equal(true); RuleFor(x => x.AcceptTerms).Equal(true);
    }
}
public sealed class LinkDecisionValidator : AbstractValidator<LinkDecisionInput>
{
    public LinkDecisionValidator()
    {
        RuleFor(x => x.Accept).NotNull(); RuleFor(x => x.Version).Must(LifecycleValidation.Version);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.SamePremises).Equal(true).When(x => x.Accept == true); RuleFor(x => x.AcceptTerms).Equal(true).When(x => x.Accept == true);
    }
}
public sealed class LinkWithdrawValidator : AbstractValidator<LinkWithdrawInput>
{
    public LinkWithdrawValidator() { RuleFor(x => x.Version).Must(LifecycleValidation.Version); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000); }
}
public sealed class CreateLinkValidator : AbstractValidator<CreateLinkCommand>
{ public CreateLinkValidator() => RuleFor(x => x.Input).NotNull().SetValidator(new LinkCreateValidator()); }
public sealed class DecideLinkValidator : AbstractValidator<DecideLinkCommand>
{ public DecideLinkValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new LinkDecisionValidator()); } }
public sealed class WithdrawLinkValidator : AbstractValidator<WithdrawLinkCommand>
{ public WithdrawLinkValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new LinkWithdrawValidator()); } }
public sealed class CreateLinkHandler(IPracticeLinkService service) : IRequestHandler<CreateLinkCommand, Result<LinkRequestDto>>
{ public Task<Result<LinkRequestDto>> Handle(CreateLinkCommand r, CancellationToken ct) => service.CreateAsync(r.Input, ct); }
public sealed class DecideLinkHandler(IPracticeLinkService service) : IRequestHandler<DecideLinkCommand, Result<LinkRequestDto>>
{ public Task<Result<LinkRequestDto>> Handle(DecideLinkCommand r, CancellationToken ct) => service.DecideAsync(r.Id, r.Input, ct); }
public sealed class WithdrawLinkHandler(IPracticeLinkService service) : IRequestHandler<WithdrawLinkCommand, Result<LinkRequestDto>>
{ public Task<Result<LinkRequestDto>> Handle(WithdrawLinkCommand r, CancellationToken ct) => service.WithdrawAsync(r.Id, r.Input, ct); }
