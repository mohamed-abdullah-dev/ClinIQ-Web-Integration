using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Appointments.Commands;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Admin")]
[Route("api/admin/booking-notices")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminBookingNoticesController(ISender sender) : ApiControllerBase
{
    [HttpPost("retry")]
    [ProducesResponseType<BookingNoticeRetryResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(CancellationToken ct)
    { var result = await sender.Send(new RetryBookingNoticesCommand(), ct); return result.Match<IActionResult>(Ok, Problem); }
}
