using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Appointments.Commands;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Roles = "Patient")]
[Route("api/patients/me/appointments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(32 * 1024)]
public sealed class PatientAppointmentsController(ISender sender) : ApiControllerBase
{
    [HttpPost("preview")]
    [ProducesResponseType<BookingPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(BookingPreviewRequest request, CancellationToken ct)
    { var result = await sender.Send(new PreviewBookingQuery(request.SessionId), ct); return result.Match<IActionResult>(Ok, Problem); }

    [HttpPost]
    [ProducesResponseType<AppointmentOperationResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<AppointmentOperationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Book(BookingConfirmation request, CancellationToken ct)
    {
        var result = await sender.Send(new BookAppointmentCommand(request), ct);
        return result.Match<IActionResult>(r => r.Replayed ? Ok(r) : CreatedAtAction(nameof(Get), new { id = r.Appointment.Id }, r), Problem);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var result = await sender.Send(new GetAppointmentQuery(id), ct); return result.Match<IActionResult>(Ok, Problem); }

    [HttpGet]
    [ProducesResponseType<AppointmentPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20)
    { var result = await sender.Send(new ListAppointmentsQuery(page, pageSize), ct); return result.Match<IActionResult>(Ok, Problem); }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<AppointmentOperationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelAppointmentRequest request, CancellationToken ct)
    { var result = await sender.Send(new CancelAppointmentCommand(id, request.RequestId, request.Reason), ct); return result.Match<IActionResult>(Ok, Problem); }

    [HttpPost("{id:guid}/reschedule/preview")]
    [ProducesResponseType<BookingPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewReschedule(Guid id, BookingPreviewRequest request, CancellationToken ct)
    { var result = await sender.Send(new PreviewBookingQuery(request.SessionId, id), ct); return result.Match<IActionResult>(Ok, Problem); }

    [HttpPost("{id:guid}/reschedule")]
    [ProducesResponseType<AppointmentOperationResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<AppointmentOperationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reschedule(Guid id, BookingConfirmation request, CancellationToken ct)
    {
        var result = await sender.Send(new RescheduleAppointmentCommand(id, request), ct);
        return result.Match<IActionResult>(r => r.Replayed ? Ok(r) : CreatedAtAction(nameof(Get), new { id = r.Appointment.Id }, r), Problem);
    }
}
public sealed record BookingPreviewRequest(Guid SessionId);
public sealed record CancelAppointmentRequest(Guid RequestId, string Reason);
