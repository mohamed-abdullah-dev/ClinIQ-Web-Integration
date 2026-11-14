using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using ClinIQ.Application.Features.Invitations.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb")]
[Route("api/workforce")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkforceController(ISender sender) : ApiControllerBase
{
    [HttpGet("receptionist-assignments")]
    [ProducesResponseType<IReadOnlyList<AssignmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Assignments(CancellationToken ct)
    { var r = await sender.Send(new ListAssignmentsQuery(), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("receptionist-assignments/{id:guid}/revoke")]
    [ProducesResponseType<AssignmentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(Guid id, InvitationRevocationRequest request, CancellationToken ct)
    { var r = await sender.Send(new RevokeReceptionistAssignmentCommand(id, request.Version, request.Reason), ct); return r.Match<IActionResult>(Ok, Problem); }
    [Authorize(Roles = "Receptionist")]
    [HttpGet("reception/doctors")]
    [ProducesResponseType<IReadOnlyList<AuthorizedDoctorDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Doctors(CancellationToken ct)
    { var r = await sender.Send(new AuthorizedDoctorsQuery(), ct); return r.Match<IActionResult>(Ok, Problem); }
    [Authorize(Roles = "Receptionist")]
    [HttpGet("reception/doctors/{doctorId:guid}/locations")]
    [ProducesResponseType<IReadOnlyList<AuthorizedPlaceDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Places(Guid doctorId, CancellationToken ct)
    { var r = await sender.Send(new AuthorizedDoctorPlacesQuery(doctorId), ct); return r.Match<IActionResult>(Ok, Problem); }
    [Authorize(Roles = "ClinicManager")]
    [HttpPut("locations/{locationId:guid}/fee")]
    [ProducesResponseType<FeeChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Fee(Guid locationId, ManagedFeeRequest request, CancellationToken ct)
    { var r = await sender.Send(new ChangeManagedFeeCommand(locationId, request.Amount!.Value, request.Currency, request.Version), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("locations/{locationId:guid}/fee-changes")]
    [ProducesResponseType<IReadOnlyList<FeeChangeDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FeeHistory(Guid locationId, CancellationToken ct)
    { var r = await sender.Send(new FeeHistoryQuery(locationId), ct); return r.Match<IActionResult>(Ok, Problem); }
    [Authorize(Roles = "ClinicManager")]
    [HttpPost("fee-changes/{id:guid}/retry-delivery")]
    [ProducesResponseType<FeeChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    { var r = await sender.Send(new RetryFeeNoticeCommand(id), ct); return r.Match<IActionResult>(Ok, Problem); }
}
public sealed record ManagedFeeRequest([System.ComponentModel.DataAnnotations.Required] decimal? Amount, string Currency, string Version);
