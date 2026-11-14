using ClinIQ.Application.Features.Specialties;
using ClinIQ.Application.Features.Specialties.Commands.CreateSpecialty;
using ClinIQ.Application.Features.Specialties.Commands.DeleteSpecialty;
using ClinIQ.Application.Features.Specialties.Commands.RenameSpecialty;
using ClinIQ.Application.Features.Specialties.Queries.Admin;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Route("api/admin/specialties")]
[Authorize(Roles = "Admin")]
[RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminSpecialtiesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AdminSpecialtyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await sender.Send(new ListAdminSpecialtiesQuery(), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AdminSpecialtyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetAdminSpecialtyQuery(id), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpPost]
    [ProducesResponseType<AdminSpecialtyDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateSpecialtyRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateSpecialtyCommand(request.NameAr.Trim(), request.NameEn.Trim()), ct);
        return result.Match<IActionResult>(row => CreatedAtAction(nameof(Get), new { id = row.Id }, row), Problem);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<AdminSpecialtyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(Guid id, RenameSpecialtyRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new RenameSpecialtyCommand(id, request.NameAr.Trim(), request.NameEn.Trim(), request.Version), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string version, CancellationToken ct)
    {
        var result = await sender.Send(new DeleteSpecialtyCommand(id, version), ct);
        return result.Match<IActionResult>(_ => NoContent(), Problem);
    }
}

public sealed record CreateSpecialtyRequest(string NameAr, string NameEn);
public sealed record RenameSpecialtyRequest(string NameAr, string NameEn, string Version);
