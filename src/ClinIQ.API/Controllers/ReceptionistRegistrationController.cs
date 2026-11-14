using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinIQ.API.Controllers;

[AllowAnonymous]
[Route("api/auth/receptionists")]
[EnableRateLimiting("auth-registration")]
[RequestSizeLimit(16 * 1024)]
public sealed class ReceptionistRegistrationController(ISender sender) : ApiControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<ReceptionistRegistrationReceipt>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterReceptionistCommand request, CancellationToken ct)
    { var r = await sender.Send(request, ct); return r.Match<IActionResult>(row => StatusCode(StatusCodes.Status201Created, row), Problem); }
}
