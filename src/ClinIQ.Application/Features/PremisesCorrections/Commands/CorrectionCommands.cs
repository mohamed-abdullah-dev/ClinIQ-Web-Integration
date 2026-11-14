using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Application.Features.PracticeLocations.Commands;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PremisesCorrections;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.PremisesCorrections.Commands;

public sealed record SubmitPremisesCorrectionCommand(Guid TargetId, bool Branch, PremisesDetails Proposed, string Reason,
    string TargetVersion, IReadOnlyList<PracticeLocationUpload> Documents) : IRequest<Result<PremisesCorrectionDto>>;
public sealed record ReviewPremisesCorrectionCommand(Guid Id, PremisesCorrectionStatus Outcome, string Reason, string Version,
    bool SamePremisesVerified, bool LicenseVerified, bool AuthorityVerified) : IRequest<Result<PremisesCorrectionDto>>;

public sealed class SubmitPremisesCorrectionValidator : AbstractValidator<SubmitPremisesCorrectionCommand>
{
    public SubmitPremisesCorrectionValidator()
    {
        RuleFor(x => x.TargetId).NotEmpty(); RuleFor(x => x.TargetVersion).Must(PracticeLocationValidation.IsVersion);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Proposed).NotNull();
        RuleFor(x => x).Must(x => x.Proposed is not null && x.Proposed.Normalize(x.Branch).IsSuccess).WithMessage("Provide valid correction details.");
        RuleFor(x => x.Documents).NotNull().Must(x => x is { Count: >= 1 and <= 2 });
    }
}
public sealed class ReviewPremisesCorrectionValidator : AbstractValidator<ReviewPremisesCorrectionCommand>
{
    public ReviewPremisesCorrectionValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Version).Must(PracticeLocationValidation.IsVersion);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Outcome).Must(x => x is PremisesCorrectionStatus.Approved or PremisesCorrectionStatus.Rejected or PremisesCorrectionStatus.NeedsInformation);
        RuleFor(x => x.SamePremisesVerified).Equal(true).When(x => x.Outcome == PremisesCorrectionStatus.Approved);
        RuleFor(x => x.LicenseVerified).Equal(true).When(x => x.Outcome == PremisesCorrectionStatus.Approved);
        RuleFor(x => x.AuthorityVerified).Equal(true).When(x => x.Outcome == PremisesCorrectionStatus.Approved);
    }
}
public sealed class SubmitPremisesCorrectionHandler(IPremisesCorrectionService service) : IRequestHandler<SubmitPremisesCorrectionCommand, Result<PremisesCorrectionDto>>
{
    public Task<Result<PremisesCorrectionDto>> Handle(SubmitPremisesCorrectionCommand r, CancellationToken ct) =>
        service.SubmitAsync(r.TargetId, r.Branch, r.Proposed, r.Reason, r.TargetVersion, r.Documents, ct);
}
public sealed class ReviewPremisesCorrectionHandler(IPremisesCorrectionService service) : IRequestHandler<ReviewPremisesCorrectionCommand, Result<PremisesCorrectionDto>>
{
    public Task<Result<PremisesCorrectionDto>> Handle(ReviewPremisesCorrectionCommand r, CancellationToken ct) =>
        service.ReviewAsync(r.Id, r.Outcome, r.Reason, r.Version, r.SamePremisesVerified, r.LicenseVerified, r.AuthorityVerified, ct);
}
