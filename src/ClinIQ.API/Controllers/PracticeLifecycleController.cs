using ClinIQ.Application.Features.PracticeLifecycle;
using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Application.Features.PracticeLifecycle.Queries;
using ClinIQ.Domain.PracticeLifecycle;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor,ClinicManager")]
[Route("api/operations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class PracticeLifecycleController(ISender sender) : ApiControllerBase
{
    [HttpGet("locations/{id:guid}")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> Location(Guid id, CancellationToken ct) => Scope(OperationalScopeKind.Location, id, ct);
    [HttpGet("branches/{id:guid}")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> Branch(Guid id, CancellationToken ct) => Scope(OperationalScopeKind.Branch, id, ct);
    private async Task<IActionResult> Scope(OperationalScopeKind kind, Guid id, CancellationToken ct)
    { var r = await sender.Send(new LifecycleScopeQuery(kind, id), ct); return r.Match<IActionResult>(Ok, Problem); }

    [HttpPost("locations/{id:guid}/departure")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> BeginDeparture(Guid id, LifecycleChangeInput input, CancellationToken ct) => Change(OperationalScopeKind.Location, id, false, input, ct);
    [HttpPost("branches/{id:guid}/closure")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> BeginClosure(Guid id, LifecycleChangeInput input, CancellationToken ct) => Change(OperationalScopeKind.Branch, id, false, input, ct);
    [HttpPost("locations/{id:guid}/finalize")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> EndDeparture(Guid id, LifecycleChangeInput input, CancellationToken ct) => Change(OperationalScopeKind.Location, id, true, input, ct);
    [HttpPost("branches/{id:guid}/finalize")]
    [ProducesResponseType<LifecycleScopeDto>(200)]
    public Task<IActionResult> EndClosure(Guid id, LifecycleChangeInput input, CancellationToken ct) => Change(OperationalScopeKind.Branch, id, true, input, ct);
    private async Task<IActionResult> Change(OperationalScopeKind kind, Guid id, bool final, LifecycleChangeInput input, CancellationToken ct)
    { var r = await sender.Send(new ChangeLifecycleCommand(kind, id, final, input), ct); return r.Match<IActionResult>(Ok, Problem); }

    [HttpGet("locations/{id:guid}/history")]
    [ProducesResponseType<LifecyclePage<LifecycleHistoryDto>>(200)]
    public Task<IActionResult> LocationHistory(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) => History(OperationalScopeKind.Location, id, page, pageSize, ct);
    [HttpGet("branches/{id:guid}/history")]
    [ProducesResponseType<LifecyclePage<LifecycleHistoryDto>>(200)]
    public Task<IActionResult> BranchHistory(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) => History(OperationalScopeKind.Branch, id, page, pageSize, ct);
    private async Task<IActionResult> History(OperationalScopeKind kind, Guid id, int page, int size, CancellationToken ct)
    { var r = await sender.Send(new LifecycleHistoryQuery(kind, id, page, size), ct); return r.Match<IActionResult>(Ok, Problem); }

    [HttpPost("locations/{id:guid}/emergencies")]
    [ProducesResponseType<EmergencySummary>(201)]
    public Task<IActionResult> RequestLocation(Guid id, EmergencyRequestInput input, CancellationToken ct) => SubmitEmergency(OperationalScopeKind.Location, id, input, ct);
    [HttpPost("branches/{id:guid}/emergencies")]
    [ProducesResponseType<EmergencySummary>(201)]
    public Task<IActionResult> RequestBranch(Guid id, EmergencyRequestInput input, CancellationToken ct) => SubmitEmergency(OperationalScopeKind.Branch, id, input, ct);
    private async Task<IActionResult> SubmitEmergency(OperationalScopeKind kind, Guid id, EmergencyRequestInput input, CancellationToken ct)
    { var r = await sender.Send(new RequestEmergencyCommand(kind, id, input), ct); return r.Match<IActionResult>(x => CreatedAtAction(nameof(Emergency), new { id = x.Id }, x), Problem); }
    [HttpGet("locations/{id:guid}/emergencies")]
    [ProducesResponseType<LifecyclePage<EmergencySummary>>(200)]
    public Task<IActionResult> LocationRequests(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) => Requests(OperationalScopeKind.Location, id, page, pageSize, ct);
    [HttpGet("branches/{id:guid}/emergencies")]
    [ProducesResponseType<LifecyclePage<EmergencySummary>>(200)]
    public Task<IActionResult> BranchRequests(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) => Requests(OperationalScopeKind.Branch, id, page, pageSize, ct);
    private async Task<IActionResult> Requests(OperationalScopeKind kind, Guid id, int page, int size, CancellationToken ct)
    { var r = await sender.Send(new EmergencyListQuery(false, kind, id, null, page, size), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("emergencies/{id:guid}")]
    [ProducesResponseType<EmergencyDetail>(200)]
    public async Task<IActionResult> Emergency(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new EmergencyDetailQuery(id, false, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
}
