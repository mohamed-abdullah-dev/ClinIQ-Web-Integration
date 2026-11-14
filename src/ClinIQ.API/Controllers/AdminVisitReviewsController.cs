using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Application.Features.VisitReviews.Commands;
using ClinIQ.Application.Features.VisitReviews.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ClinIQ.API.Controllers;
[Authorize(Policy="ClinicWeb",Roles="Admin")]
[Route("api/admin/visit-reviews")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[RequestSizeLimit(16*1024)]
public sealed class AdminVisitReviewsController(ISender sender):ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<VisitReviewPage<VisitReviewDto>>(200)]
    public async Task<IActionResult> List(CancellationToken ct,int page=1,int pageSize=20)
    {var r=await sender.Send(new AdminVisitReviewsQuery(page,pageSize),ct);return r.Match<IActionResult>(Ok,Problem);}
    [HttpPost("{id:guid}/moderation")]
    [ProducesResponseType<VisitReviewDto>(200)]
    public async Task<IActionResult> Moderate(Guid id,ModerateVisitReviewInput input,CancellationToken ct)
    {var r=await sender.Send(new ModerateVisitReviewCommand(id,input),ct);return r.Match<IActionResult>(Ok,Problem);}
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType<VisitReviewPage<VisitReviewAuditDto>>(200)]
    public async Task<IActionResult> History(Guid id,CancellationToken ct,int page=1,int pageSize=20)
    {var r=await sender.Send(new VisitReviewHistoryQuery(id,page,pageSize),ct);return r.Match<IActionResult>(Ok,Problem);}
}
