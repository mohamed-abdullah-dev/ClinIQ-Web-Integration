using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PremisesCorrections;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.PremisesCorrections.Queries;

public sealed record ListPremisesCorrectionsQuery(bool Admin, Guid? LocationId, Guid? BranchId, PremisesCorrectionStatus? Status,
    int Page = 1, int PageSize = 20) : IRequest<Result<PremisesCorrectionPage>>;
public sealed record GetPremisesCorrectionQuery(Guid Id, bool Admin) : IRequest<Result<PremisesCorrectionDto>>;
public sealed record DownloadCorrectionEvidenceQuery(Guid Id, Guid DocumentId, bool Admin) : IRequest<Result<PracticeLocationEvidenceDownload>>;
public sealed class ListPremisesCorrectionsValidator : AbstractValidator<ListPremisesCorrectionsQuery>
{
    public ListPremisesCorrectionsValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 1000000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.LocationId).NotEqual(Guid.Empty).When(x => x.LocationId.HasValue);
        RuleFor(x => x.BranchId).NotEqual(Guid.Empty).When(x => x.BranchId.HasValue);
        RuleFor(x => x).Must(x => x.LocationId is null || x.BranchId is null).WithMessage("Filter by one place type.");
    }
}
public sealed class GetPremisesCorrectionValidator : AbstractValidator<GetPremisesCorrectionQuery>
{ public GetPremisesCorrectionValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class DownloadCorrectionEvidenceValidator : AbstractValidator<DownloadCorrectionEvidenceQuery>
{
    public DownloadCorrectionEvidenceValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.DocumentId).NotEmpty(); }
}
public sealed class ListPremisesCorrectionsHandler(IPremisesCorrectionService service) : IRequestHandler<ListPremisesCorrectionsQuery, Result<PremisesCorrectionPage>>
{
    public Task<Result<PremisesCorrectionPage>> Handle(ListPremisesCorrectionsQuery r, CancellationToken ct) =>
        service.ListAsync(r.Admin, r.LocationId, r.BranchId, r.Status, r.Page, r.PageSize, ct);
}
public sealed class GetPremisesCorrectionHandler(IPremisesCorrectionService service) : IRequestHandler<GetPremisesCorrectionQuery, Result<PremisesCorrectionDto>>
{ public Task<Result<PremisesCorrectionDto>> Handle(GetPremisesCorrectionQuery r, CancellationToken ct) => service.GetAsync(r.Id, r.Admin, ct); }
public sealed class DownloadCorrectionEvidenceHandler(IPremisesCorrectionService service) : IRequestHandler<DownloadCorrectionEvidenceQuery, Result<PracticeLocationEvidenceDownload>>
{ public Task<Result<PracticeLocationEvidenceDownload>> Handle(DownloadCorrectionEvidenceQuery r, CancellationToken ct) => service.DownloadAsync(r.Id, r.DocumentId, r.Admin, ct); }
