using ClinIQ.Application.Features.Subscriptions;
using ClinIQ.Application.Features.Subscriptions.Commands;
using ClinIQ.Application.Features.Subscriptions.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor")]
[Route("api/doctors/me/subscription")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DoctorSubscriptionsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<DoctorSubscriptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetOwnDoctorSubscriptionQuery(), cancellationToken);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpPost("pro-trial")]
    [ProducesResponseType<DoctorSubscriptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ActivateProTrial(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ActivateDoctorProTrialCommand(), cancellationToken);
        return result.Match<IActionResult>(Ok, Problem);
    }
}
