using ClinIQ.Application.Features.PracticeLifecycle;
using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Application.Features.PracticeLifecycle.Queries;
using ClinIQ.Domain.PracticeLifecycle;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Admin")]
[Route("api/admin/operational-emergencies")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class AdminOperationalEmergenciesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<LifecyclePage<EmergencySummary>>(200)]
    public async Task<IActionResult> List(CancellationToken ct, EmergencyRequestStatus? status = null, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new EmergencyListQuery(true, null, null, status ?? EmergencyRequestStatus.Pending, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<EmergencyDetail>(200)]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new EmergencyDetailQuery(id, true, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/decision")]
    [ProducesResponseType<EmergencySummary>(200)]
    public async Task<IActionResult> Decide(Guid id, EmergencyDecisionInput input, CancellationToken ct)
    { var r = await sender.Send(new DecideEmergencyCommand(id, input), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}/cancellations")]
    [ProducesResponseType<LifecyclePage<EmergencyCancellationDto>>(200)]
    public async Task<IActionResult> Cancellations(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new EmergencyCancellationsQuery(id, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
}
