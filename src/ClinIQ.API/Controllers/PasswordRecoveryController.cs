using ClinIQ.Application.Features.Accounts.Commands.ForgotPassword;
using ClinIQ.Application.Features.Accounts.Commands.ResetPassword;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinIQ.API.Controllers;

[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting("auth-recovery")]
[RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PasswordRecoveryController(ISender sender) : ApiControllerBase
{
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Forgot(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(request with { Email = request.Email.Trim() }, cancellationToken);
        return result.Match<IActionResult>(_ => Accepted(), Problem);
    }
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Reset(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(request, cancellationToken);
        return result.Match<IActionResult>(_ => NoContent(), Problem);
    }
}
