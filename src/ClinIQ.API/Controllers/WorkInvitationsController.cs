using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using ClinIQ.Application.Features.Invitations.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb")]
[Route("api/work-invitations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkInvitationsController(ISender sender) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Issue(IssueInvitationCommand request, CancellationToken ct)
    { var r = await sender.Send(request, ct); return r.Match<IActionResult>(row => CreatedAtAction(nameof(Get), new { id = row.Id, received = false }, row), Problem); }
    [HttpGet]
    [ProducesResponseType<InvitationPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, bool received = true, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new ListInvitationsQuery(received, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct, bool received = true)
    { var r = await sender.Send(new GetInvitationQuery(id, received), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/resend")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend(Guid id, InvitationVersionRequest request, CancellationToken ct)
    { var r = await sender.Send(new ResendInvitationCommand(id, request.Version), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/revoke")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(Guid id, InvitationRevocationRequest request, CancellationToken ct)
    { var r = await sender.Send(new RevokeInvitationCommand(id, request.Version, request.Reason), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/accept")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Accept(Guid id, InvitationResponseRequest request, CancellationToken ct) => Respond(id, request, true, ct);
    [HttpPost("{id:guid}/decline")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Decline(Guid id, InvitationResponseRequest request, CancellationToken ct) => Respond(id, request, false, ct);
    private async Task<IActionResult> Respond(Guid id, InvitationResponseRequest request, bool accept, CancellationToken ct)
    { var r = await sender.Send(new RespondInvitationCommand(id, request.Secret, request.Version, accept), ct); return r.Match<IActionResult>(Ok, Problem); }
}
public sealed record InvitationVersionRequest(string Version);
public sealed record InvitationRevocationRequest(string Version, string Reason);
public sealed record InvitationResponseRequest(string Secret, string Version);
