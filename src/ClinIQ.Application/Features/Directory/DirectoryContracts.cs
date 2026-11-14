using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;

namespace ClinIQ.Application.Features.Directory;

public enum DirectorySort { NextAvailable = 1, PriceAscending = 2, PriceDescending = 3, Distance = 4 }
public record DirectoryWindow
{
    public DateOnly? From { get; init; }
    public DateOnly? Until { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
public sealed record DirectoryFilter : DirectoryWindow
{
    public Guid? SpecialtyId { get; init; }
    public string? Name { get; init; }
    public string? Area { get; init; }
    public decimal? MinFee { get; init; }
    public decimal? MaxFee { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public double? MaxDistanceKm { get; init; }
    public DirectorySort Sort { get; init; } = DirectorySort.NextAvailable;
}
public sealed record DirectoryPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize, DateTimeOffset AsOfUtc);
public sealed record PublicSpecialty(Guid Id, string NameAr, string NameEn);
public sealed record PublicDoctor(Guid Id, string ProfessionalName, ProfessionalGrade ProfessionalGrade, PublicSpecialty Specialty,
    double? ReviewAverage, int ReviewCount);
public sealed record PublicPlace(Guid Id, Guid DoctorProfileId, Guid? ClinicId, string? ClinicName, Guid? BranchId,
    string Name, string Address, decimal? Latitude, decimal? Longitude, string? PublicPhone, decimal ConsultationFee, string Currency);
public sealed record PublicSession(Guid Id, Guid LocationId, DateOnly LocalDate, TimeOnly StartsLocal, TimeOnly EndsLocal,
    string TimeZoneId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, DateTimeOffset BookingClosesAtUtc, int RemainingCapacity);
public sealed record DoctorOption(PublicDoctor Doctor, PublicPlace Place, PublicSession NextSession, double? DistanceKm);
public sealed record PublicLocationSummary(PublicPlace Place, PublicSession? NextSession);
public sealed record PublicDoctorProfile(PublicDoctor Doctor, DirectoryPage<PublicLocationSummary> Locations);
public sealed record PublicLocationProfile(PublicDoctor Doctor, PublicPlace Place, DirectoryPage<PublicSession> Sessions);
public sealed record PublicSessionProfile(PublicDoctor Doctor, PublicPlace Place, PublicSession Session, DateTimeOffset AsOfUtc);
public sealed record PublicBranch(Guid Id, Guid ClinicId, string Name, string Address, decimal? Latitude, decimal? Longitude,
    string PublicPhone, string TimeZoneId, int BookableDoctorCount);
public sealed record PublicClinicProfile(Guid Id, string Name, string Description, DirectoryPage<PublicBranch> Branches);
public sealed record PublicBranchProfile(PublicBranch Branch, DirectoryPage<DoctorOption> Doctors);
public interface IPublicDirectoryService
{
    Task<Result<DirectoryPage<DoctorOption>>> SearchAsync(DirectoryFilter filter, CancellationToken ct);
    Task<Result<PublicDoctorProfile>> DoctorAsync(Guid id, DirectoryWindow filter, CancellationToken ct);
    Task<Result<PublicLocationProfile>> LocationAsync(Guid id, DirectoryWindow filter, CancellationToken ct);
    Task<Result<PublicSessionProfile>> SessionAsync(Guid id, CancellationToken ct);
    Task<Result<PublicClinicProfile>> ClinicAsync(Guid id, DirectoryWindow filter, CancellationToken ct);
    Task<Result<PublicBranchProfile>> BranchAsync(Guid clinicId, Guid branchId, DirectoryFilter filter, CancellationToken ct);
}
public static class DirectoryErrors
{
    public static readonly Error NotFound = Error.NotFound("Directory.NotFound", "The approved public resource or currently bookable session was not found.");
    public static readonly Error Dates = Error.Validation("Directory.Dates", "Choose Cairo dates from today through today plus 27 days, in ascending order.", "Dates");
}
