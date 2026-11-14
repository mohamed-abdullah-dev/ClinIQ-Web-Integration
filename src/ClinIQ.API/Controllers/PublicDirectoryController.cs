using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Directory.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[AllowAnonymous]
[Route("api/public")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicDirectoryController(ISender sender) : ApiControllerBase
{
    [HttpGet("doctors")]
    [ProducesResponseType<DirectoryPage<DoctorOption>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] DirectoryFilter filter, CancellationToken ct)
    {
        var result = await sender.Send(new SearchDirectoryQuery(filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("doctors/{id:guid}")]
    [ProducesResponseType<PublicDoctorProfile>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Doctor(Guid id, [FromQuery] DirectoryWindow filter, CancellationToken ct)
    {
        var result = await sender.Send(new PublicDoctorQuery(id, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("locations/{id:guid}")]
    [ProducesResponseType<PublicLocationProfile>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Location(Guid id, [FromQuery] DirectoryWindow filter, CancellationToken ct)
    {
        var result = await sender.Send(new PublicLocationQuery(id, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<PublicSessionProfile>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Session(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new PublicSessionQuery(id), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("clinics/{id:guid}")]
    [ProducesResponseType<PublicClinicProfile>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clinic(Guid id, [FromQuery] DirectoryWindow filter, CancellationToken ct)
    {
        var result = await sender.Send(new PublicClinicQuery(id, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("clinics/{clinicId:guid}/branches/{branchId:guid}")]
    [ProducesResponseType<PublicBranchProfile>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Branch(Guid clinicId, Guid branchId, [FromQuery] DirectoryFilter filter, CancellationToken ct)
    {
        var result = await sender.Send(new PublicBranchQuery(clinicId, branchId, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
}
