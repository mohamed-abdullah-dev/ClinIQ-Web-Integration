using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Application.Features.VisitReviews.Commands;
using ClinIQ.Application.Features.VisitReviews.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ClinIQ.API.Controllers;
[Authorize(Policy="PatientMobile")]
[Route("api/patients/me/appointments/{appointmentId:guid}/review")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[RequestSizeLimit(16*1024)]
public sealed class PatientVisitReviewsController(ISender sender):ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<VisitReviewWriteResult>(201)]
    [ProducesResponseType<VisitReviewWriteResult>(200)]
    public async Task<IActionResult> Create(Guid appointmentId,WriteVisitReviewInput input,CancellationToken ct)
    {var r=await sender.Send(new WriteVisitReviewCommand(appointmentId,input),ct);return r.Match<IActionResult>(x=>x.Replayed?Ok(x):CreatedAtAction(nameof(Get),new{appointmentId},x),Problem);}
    [HttpPut]
    [ProducesResponseType<VisitReviewWriteResult>(200)]
    public async Task<IActionResult> Edit(Guid appointmentId,WriteVisitReviewInput input,CancellationToken ct)
    {var r=await sender.Send(new WriteVisitReviewCommand(appointmentId,input,true),ct);return r.Match<IActionResult>(Ok,Problem);}
    [HttpGet]
    [ProducesResponseType<VisitReviewDto>(200)]
    public async Task<IActionResult> Get(Guid appointmentId,CancellationToken ct)
    {var r=await sender.Send(new OwnVisitReviewQuery(appointmentId),ct);return r.Match<IActionResult>(Ok,Problem);}
}
