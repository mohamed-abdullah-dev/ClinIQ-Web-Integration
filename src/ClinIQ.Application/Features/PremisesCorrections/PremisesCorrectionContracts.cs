using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;

namespace ClinIQ.Application.Features.PremisesCorrections;

public sealed record PremisesCorrectionDto(Guid Id, Guid? LocationId, Guid? BranchId, Guid ApplicantUserId,
    PremisesDetails Original, PremisesDetails Proposed, string OriginalVersion, string RequestReason, string Status,
    DateTimeOffset CreatedAtUtc, string Version, Guid? ReviewerUserId, string? ReviewReason, DateTimeOffset? ReviewedAtUtc,
    bool SamePremisesVerified, bool LicenseVerified, bool AuthorityVerified, IReadOnlyList<CorrectionEvidenceDto> Documents);
public sealed record CorrectionEvidenceDto(Guid Id, PracticeLocationEvidenceKind Kind, string FileName, string MediaType, long Length);
public sealed record PremisesCorrectionPage(IReadOnlyList<PremisesCorrectionDto> Items, int TotalCount, int Page, int PageSize);

public interface IPremisesCorrectionService
{
    Task<Result<PremisesCorrectionDto>> SubmitAsync(Guid targetId, bool branch, PremisesDetails proposed, string reason,
        string targetVersion, IReadOnlyList<PracticeLocationUpload> documents, CancellationToken ct);
    Task<Result<PremisesCorrectionPage>> ListAsync(bool admin, Guid? locationId, Guid? branchId, PremisesCorrectionStatus? status, int page, int pageSize, CancellationToken ct);
    Task<Result<PremisesCorrectionDto>> GetAsync(Guid id, bool admin, CancellationToken ct);
    Task<Result<PremisesCorrectionDto>> ReviewAsync(Guid id, PremisesCorrectionStatus outcome, string reason, string version,
        bool samePremises, bool license, bool authority, CancellationToken ct);
    Task<Result<PracticeLocationEvidenceDownload>> DownloadAsync(Guid id, Guid documentId, bool admin, CancellationToken ct);
}
