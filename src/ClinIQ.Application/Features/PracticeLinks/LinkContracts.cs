using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLinks;

namespace ClinIQ.Application.Features.PracticeLinks;

public sealed record LinkSourceDto(Guid LocationId, Guid BranchId, string LocationName, string LocationAddress,
    string BranchName, string BranchAddress, decimal ConsultationFee, string Currency, string LocationVersion, string BranchVersion, string Terms);
public sealed record LinkRequestDto(Guid Id, Guid DoctorUserId, LinkSourceDto Source, PracticeLinkStatus Status,
    DateTimeOffset RequestedAtUtc, Guid? DecidedByUserId, DateTimeOffset? DecidedAtUtc, string? DecisionReason, string Version);
public sealed record LinkPage(IReadOnlyList<LinkRequestDto> Items, int TotalCount, int Page, int PageSize);
public sealed record LinkCreateInput(Guid LocationId, Guid BranchId, string LocationVersion, string BranchVersion, bool SamePremises, bool AcceptTerms);
public sealed record LinkDecisionInput(bool? Accept, string Version, string Reason, bool SamePremises, bool AcceptTerms);
public sealed record LinkWithdrawInput(string Version, string Reason);
public interface IPracticeLinkService
{
    Task<Result<LinkSourceDto>> PreviewAsync(Guid location, Guid branch, CancellationToken ct);
    Task<Result<LinkRequestDto>> CreateAsync(LinkCreateInput input, CancellationToken ct);
    Task<Result<LinkRequestDto>> DecideAsync(Guid id, LinkDecisionInput input, CancellationToken ct);
    Task<Result<LinkRequestDto>> WithdrawAsync(Guid id, LinkWithdrawInput input, CancellationToken ct);
    Task<Result<LinkRequestDto>> GetAsync(Guid id, CancellationToken ct);
    Task<Result<LinkPage>> ListAsync(PracticeLinkStatus? status, int page, int size, CancellationToken ct);
}
