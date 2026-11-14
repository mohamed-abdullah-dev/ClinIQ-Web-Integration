using ClinIQ.Application.Features.Patients;
using ClinIQ.Application.Features.Patients.Commands.SaveOwnProfile;
using ClinIQ.Application.Features.Patients.Queries.GetOwnProfile;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Route("api/patients/me/profile")]
[Authorize(Roles = "Patient")]
[RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PatientProfilesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PatientProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await sender.Send(new GetOwnPatientProfileQuery(), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpPut]
    [ProducesResponseType<PatientProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<PatientProfileDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Save(SavePatientProfileRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new SaveOwnPatientProfileCommand(request.FullName.Trim(), request.PhoneNumber.Trim(), request.Version), ct);
        return result.Match<IActionResult>(saved => saved.Created
            ? CreatedAtAction(nameof(Get), saved.Profile)
            : Ok(saved.Profile), Problem);
    }
}

public sealed record SavePatientProfileRequest(string FullName, string PhoneNumber, string? Version = null);
