using ClinIQ.Domain.VisitReviews;
using ClinIQ.Domain.Common.Results;
namespace ClinIQ.Application.Features.VisitReviews;
public sealed record WriteVisitReviewInput(int Stars,string? Text,string? Version=null);
public sealed record ModerateVisitReviewInput(bool Hide,ReviewModerationCategory? Category,string Reason,string Version);
public sealed record VisitReviewDto(Guid Id,Guid AppointmentId,Guid DoctorProfileId,Guid LocationId,int Stars,string? Text,bool Hidden,bool Eligible,
    DateTimeOffset CreatedAtUtc,DateTimeOffset? EditedAtUtc,DateTimeOffset? EditDeadlineUtc,string Version);
public sealed record PublicVisitReview(Guid Id,Guid LocationId,string LocationName,int Stars,string? Text,DateTimeOffset CreatedAtUtc,DateTimeOffset? EditedAtUtc);
public sealed record VisitReviewPage<T>(IReadOnlyList<T> Items,int TotalCount,int Page,int PageSize);
public sealed record VisitReviewWriteResult(VisitReviewDto Review,bool Replayed);
public sealed record VisitReviewAuditDto(int Revision,Guid ActorUserId,string Action,string BeforeJson,string AfterJson,DateTimeOffset RecordedAtUtc);
public interface IVisitReviewService
{
    Task<Result<VisitReviewWriteResult>> WriteAsync(Guid appointment,WriteVisitReviewInput input,bool edit,CancellationToken ct);
    Task<Result<VisitReviewDto>> OwnAsync(Guid appointment,CancellationToken ct);
    Task<Result<VisitReviewPage<PublicVisitReview>>> PublicAsync(Guid doctor,int page,int size,CancellationToken ct);
    Task<Result<VisitReviewPage<VisitReviewDto>>> AdminAsync(int page,int size,CancellationToken ct);
    Task<Result<VisitReviewDto>> ModerateAsync(Guid id,ModerateVisitReviewInput input,CancellationToken ct);
    Task<Result<VisitReviewPage<VisitReviewAuditDto>>> HistoryAsync(Guid id,int page,int size,CancellationToken ct);
}
