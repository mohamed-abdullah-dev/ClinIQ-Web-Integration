using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLocations;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Directory.Queries;

public sealed record SearchDirectoryQuery(DirectoryFilter Filter) : IRequest<Result<DirectoryPage<DoctorOption>>>;
public sealed record PublicDoctorQuery(Guid Id, DirectoryWindow Filter) : IRequest<Result<PublicDoctorProfile>>;
public sealed record PublicLocationQuery(Guid Id, DirectoryWindow Filter) : IRequest<Result<PublicLocationProfile>>;
public sealed record PublicSessionQuery(Guid Id) : IRequest<Result<PublicSessionProfile>>;
public sealed record PublicClinicQuery(Guid Id, DirectoryWindow Filter) : IRequest<Result<PublicClinicProfile>>;
public sealed record PublicBranchQuery(Guid ClinicId, Guid BranchId, DirectoryFilter Filter) : IRequest<Result<PublicBranchProfile>>;
public sealed class DirectoryWindowValidator : AbstractValidator<DirectoryWindow>
{
    public DirectoryWindowValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 100000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
public sealed class DirectoryFilterValidator : AbstractValidator<DirectoryFilter>
{
    public DirectoryFilterValidator()
    {
        Include(new DirectoryWindowValidator());
        RuleFor(x => x.SpecialtyId).NotEqual(Guid.Empty).When(x => x.SpecialtyId.HasValue);
        RuleFor(x => x.Name).MaximumLength(100); RuleFor(x => x.Area).MaximumLength(100);
        RuleFor(x => x.MinFee).GreaterThanOrEqualTo(0).LessThanOrEqualTo(ConsultationPrice.MaxAmount).PrecisionScale(18, 2, true).When(x => x.MinFee.HasValue);
        RuleFor(x => x.MaxFee).GreaterThanOrEqualTo(0).LessThanOrEqualTo(ConsultationPrice.MaxAmount).PrecisionScale(18, 2, true).When(x => x.MaxFee.HasValue);
        RuleFor(x => x).Must(x => x.MinFee is null || x.MaxFee is null || x.MinFee <= x.MaxFee).WithMessage("Minimum fee cannot exceed maximum fee.");
        RuleFor(x => x).Must(x => x.Latitude.HasValue == x.Longitude.HasValue && x.Latitude is not (< -90 or > 90) && x.Longitude is not (< -180 or > 180)).WithMessage("Provide both valid coordinates or omit both.");
        RuleFor(x => x.MaxDistanceKm).Must(x => x.HasValue && double.IsFinite(x.Value) && x.Value > 0 && x.Value <= 1000).When(x => x.MaxDistanceKm.HasValue);
        RuleFor(x => x).Must(x => x.Latitude.HasValue).When(x => x.MaxDistanceKm.HasValue || x.Sort == DirectorySort.Distance).WithMessage("Distance filtering/sorting requires explicit coordinates.");
        RuleFor(x => x.Sort).IsInEnum();
    }
}
public sealed class SearchDirectoryValidator : AbstractValidator<SearchDirectoryQuery>
{ public SearchDirectoryValidator() => RuleFor(x => x.Filter).NotNull().SetValidator(new DirectoryFilterValidator()); }
public sealed class PublicDoctorValidator : AbstractValidator<PublicDoctorQuery>
{ public PublicDoctorValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Filter).NotNull().SetValidator(new DirectoryWindowValidator()); } }
public sealed class PublicLocationValidator : AbstractValidator<PublicLocationQuery>
{ public PublicLocationValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Filter).NotNull().SetValidator(new DirectoryWindowValidator()); } }
public sealed class PublicSessionValidator : AbstractValidator<PublicSessionQuery>
{ public PublicSessionValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class PublicClinicValidator : AbstractValidator<PublicClinicQuery>
{ public PublicClinicValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Filter).NotNull().SetValidator(new DirectoryWindowValidator()); } }
public sealed class PublicBranchValidator : AbstractValidator<PublicBranchQuery>
{ public PublicBranchValidator() { RuleFor(x => x.ClinicId).NotEmpty(); RuleFor(x => x.BranchId).NotEmpty(); RuleFor(x => x.Filter).NotNull().SetValidator(new DirectoryFilterValidator()); } }
public sealed class SearchDirectoryHandler(IPublicDirectoryService s) : IRequestHandler<SearchDirectoryQuery, Result<DirectoryPage<DoctorOption>>>
{ public Task<Result<DirectoryPage<DoctorOption>>> Handle(SearchDirectoryQuery r, CancellationToken ct) => s.SearchAsync(r.Filter, ct); }
public sealed class PublicDoctorHandler(IPublicDirectoryService s) : IRequestHandler<PublicDoctorQuery, Result<PublicDoctorProfile>>
{ public Task<Result<PublicDoctorProfile>> Handle(PublicDoctorQuery r, CancellationToken ct) => s.DoctorAsync(r.Id, r.Filter, ct); }
public sealed class PublicLocationHandler(IPublicDirectoryService s) : IRequestHandler<PublicLocationQuery, Result<PublicLocationProfile>>
{ public Task<Result<PublicLocationProfile>> Handle(PublicLocationQuery r, CancellationToken ct) => s.LocationAsync(r.Id, r.Filter, ct); }
public sealed class PublicSessionHandler(IPublicDirectoryService s) : IRequestHandler<PublicSessionQuery, Result<PublicSessionProfile>>
{ public Task<Result<PublicSessionProfile>> Handle(PublicSessionQuery r, CancellationToken ct) => s.SessionAsync(r.Id, ct); }
public sealed class PublicClinicHandler(IPublicDirectoryService s) : IRequestHandler<PublicClinicQuery, Result<PublicClinicProfile>>
{ public Task<Result<PublicClinicProfile>> Handle(PublicClinicQuery r, CancellationToken ct) => s.ClinicAsync(r.Id, r.Filter, ct); }
public sealed class PublicBranchHandler(IPublicDirectoryService s) : IRequestHandler<PublicBranchQuery, Result<PublicBranchProfile>>
{ public Task<Result<PublicBranchProfile>> Handle(PublicBranchQuery r, CancellationToken ct) => s.BranchAsync(r.ClinicId, r.BranchId, r.Filter, ct); }
