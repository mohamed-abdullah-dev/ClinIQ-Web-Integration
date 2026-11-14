using ClinIQ.Application.Features.PracticeLinks;
using ClinIQ.Application.Features.PracticeLinks.Commands;
using ClinIQ.Application.Features.PracticeLinks.Queries;
using ClinIQ.Domain.PracticeLinks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor,ClinicManager")]
[Route("api/practice-links")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class PracticeLinksController(ISender sender) : ApiControllerBase
{
    [HttpGet("preview")]
    [ProducesResponseType<LinkSourceDto>(200)]
    public async Task<IActionResult> Preview(Guid locationId, Guid branchId, CancellationToken ct)
    { var r = await sender.Send(new LinkPreviewQuery(locationId, branchId), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost]
    [ProducesResponseType<LinkRequestDto>(201)]
    public async Task<IActionResult> Create(LinkCreateInput input, CancellationToken ct)
    { var r = await sender.Send(new CreateLinkCommand(input), ct); return r.Match<IActionResult>(x => CreatedAtAction(nameof(Get), new { id = x.Id }, x), Problem); }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<LinkRequestDto>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var r = await sender.Send(new LinkQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet]
    [ProducesResponseType<LinkPage>(200)]
    public async Task<IActionResult> List(CancellationToken ct, PracticeLinkStatus? status = null, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new LinkListQuery(status, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/decision")]
    [ProducesResponseType<LinkRequestDto>(200)]
    public async Task<IActionResult> Decide(Guid id, LinkDecisionInput input, CancellationToken ct)
    { var r = await sender.Send(new DecideLinkCommand(id, input), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/withdraw")]
    [ProducesResponseType<LinkRequestDto>(200)]
    public async Task<IActionResult> Withdraw(Guid id, LinkWithdrawInput input, CancellationToken ct)
    { var r = await sender.Send(new WithdrawLinkCommand(id, input), ct); return r.Match<IActionResult>(Ok, Problem); }
}
