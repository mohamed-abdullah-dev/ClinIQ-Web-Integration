using ClinIQ.Application.Features.Notifications;
using ClinIQ.Application.Features.Notifications.Commands;
using ClinIQ.Application.Features.Notifications.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ClinIQ.API.Controllers;
[Authorize(Policy = "ClinicWeb", Roles = "Admin")]
[Route("api/admin/feedback-deliveries")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminFeedbackNotificationsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FeedbackDeliveryDto>>(200)]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackDeliveriesQuery(page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("process")]
    [ProducesResponseType<FeedbackDispatchResult>(200)]
    public async Task<IActionResult> Process(CancellationToken ct)
    { var r = await sender.Send(new ProcessFeedbackNotificationsCommand(), ct); return r.Match<IActionResult>(Ok, Problem); }
}
