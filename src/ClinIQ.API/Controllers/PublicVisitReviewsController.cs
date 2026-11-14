using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Application.Features.VisitReviews.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ClinIQ.API.Controllers;
[AllowAnonymous]
[Route("api/public/doctors/{doctorId:guid}/reviews")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class PublicVisitReviewsController(ISender sender):ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<VisitReviewPage<PublicVisitReview>>(200)]
    public async Task<IActionResult> Get(Guid doctorId,CancellationToken ct,int page=1,int pageSize=20)
    {var r=await sender.Send(new PublicVisitReviewsQuery(doctorId,page,pageSize),ct);return r.Match<IActionResult>(Ok,Problem);}
}
