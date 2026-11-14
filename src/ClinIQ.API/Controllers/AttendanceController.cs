using System.ComponentModel.DataAnnotations;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.Attendance.Commands;
using ClinIQ.Application.Features.Attendance.Queries;
using ClinIQ.Domain.Appointments;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor,Receptionist")]
[Route("api/attendance/doctors")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class AttendanceController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AttendanceDoctor>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Doctors(CancellationToken ct)
    { var r = await sender.Send(new AttendanceDoctorsQuery(), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{doctorId:guid}/locations")]
    [ProducesResponseType<IReadOnlyList<AttendancePlace>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Places(Guid doctorId, CancellationToken ct)
    { var r = await sender.Send(new AttendancePlacesQuery(doctorId), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{doctorId:guid}/locations/{locationId:guid}/sessions")]
    [ProducesResponseType<AttendancePage<AttendanceSession>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions(Guid doctorId, Guid locationId, [FromQuery, Required] DateOnly? from, [FromQuery, Required] DateOnly? until, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new AttendanceSessionsQuery(doctorId, locationId, from!.Value, until!.Value, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{doctorId:guid}/sessions/{sessionId:guid}/appointments")]
    [ProducesResponseType<AttendanceQueue>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Queue(Guid doctorId, Guid sessionId, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new AttendanceQueueQuery(doctorId, sessionId, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/history")]
    [ProducesResponseType<AttendancePage<AttendanceHistory>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(Guid doctorId, Guid sessionId, Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new AttendanceHistoryQuery(doctorId, sessionId, id, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/check-in")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> CheckIn(Guid doctorId, Guid sessionId, Guid id, AttendanceConfirmation request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.CheckIn, request.Version), ct);
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/complete")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> Complete(Guid doctorId, Guid sessionId, Guid id, CompleteAttendanceRequest request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.Complete, request.Version, CompletedAtUtc: request.CompletedAtUtc), ct);
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/no-show")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> NoShow(Guid doctorId, Guid sessionId, Guid id, AttendanceConfirmation request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.NoShow, request.Version), ct);
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/not-seen")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> NotSeen(Guid doctorId, Guid sessionId, Guid id, ReasonedAttendanceRequest request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.NotSeen, request.Version, Reason: request.Reason), ct);
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/correct")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> Correct(Guid doctorId, Guid sessionId, Guid id, CorrectAttendanceRequest request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.Correct, request.Version, request.ArrivedAtUtc, request.CompletedAtUtc, request.Status, request.Reason), ct);
    [Authorize(Roles = "Doctor")]
    [HttpPost("{doctorId:guid}/sessions/{sessionId:guid}/appointments/{id:guid}/late-check-in")]
    [ProducesResponseType<AttendanceWriteResult>(StatusCodes.Status200OK)]
    public Task<IActionResult> LateCheckIn(Guid doctorId, Guid sessionId, Guid id, LateAttendanceRequest request, CancellationToken ct) => Write(doctorId, sessionId, id, new(request.ConfirmedPatientId, AttendanceAction.LateCheckIn, request.Version, request.ArrivedAtUtc, Reason: request.Reason), ct);
    private async Task<IActionResult> Write(Guid doctor, Guid session, Guid appointment, AttendanceInput input, CancellationToken ct)
    { var r = await sender.Send(new WriteAttendanceCommand(doctor, session, appointment, input), ct); return r.Match<IActionResult>(Ok, Problem); }
}
public sealed record AttendanceConfirmation(Guid ConfirmedPatientId, string Version);
public sealed record CompleteAttendanceRequest(Guid ConfirmedPatientId, string Version, DateTimeOffset? CompletedAtUtc);
public sealed record ReasonedAttendanceRequest(Guid ConfirmedPatientId, string Version, string Reason);
public sealed record CorrectAttendanceRequest(Guid ConfirmedPatientId, string Version, AppointmentStatus? Status, string Reason, DateTimeOffset? ArrivedAtUtc = null, DateTimeOffset? CompletedAtUtc = null);
public sealed record LateAttendanceRequest(Guid ConfirmedPatientId, string Version, DateTimeOffset? ArrivedAtUtc, string Reason);
