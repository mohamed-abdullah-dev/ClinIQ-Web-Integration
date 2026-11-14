using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLinks;
using MediatR;

namespace ClinIQ.Application.Features.PracticeLinks.Queries;

public sealed record LinkPreviewQuery(Guid LocationId, Guid BranchId) : IRequest<Result<LinkSourceDto>>;
public sealed record LinkQuery(Guid Id) : IRequest<Result<LinkRequestDto>>;
public sealed record LinkListQuery(PracticeLinkStatus? Status, int Page, int PageSize) : IRequest<Result<LinkPage>>;
public sealed class LinkPreviewHandler(IPracticeLinkService service) : IRequestHandler<LinkPreviewQuery, Result<LinkSourceDto>>
{ public Task<Result<LinkSourceDto>> Handle(LinkPreviewQuery r, CancellationToken ct) => service.PreviewAsync(r.LocationId, r.BranchId, ct); }
public sealed class LinkHandler(IPracticeLinkService service) : IRequestHandler<LinkQuery, Result<LinkRequestDto>>
{ public Task<Result<LinkRequestDto>> Handle(LinkQuery r, CancellationToken ct) => service.GetAsync(r.Id, ct); }
public sealed class LinkListHandler(IPracticeLinkService service) : IRequestHandler<LinkListQuery, Result<LinkPage>>
{ public Task<Result<LinkPage>> Handle(LinkListQuery r, CancellationToken ct) => service.ListAsync(r.Status, r.Page, r.PageSize, ct); }
