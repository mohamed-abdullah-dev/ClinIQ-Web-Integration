using ClinIQ.Application.Features.Specialties.Dtos;
using ClinIQ.Application.Features.Specialties.Queries.GetSpecialties;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[AllowAnonymous]
[Route("api/specialties")]
public sealed class SpecialtiesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SpecialtyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetSpecialtiesQuery(), cancellationToken);
        return result.Match<IActionResult>(specialties => Ok(specialties), Problem);
    }
}
