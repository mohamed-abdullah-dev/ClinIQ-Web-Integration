using System.ComponentModel.DataAnnotations;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Application.Features.Scheduling.Commands;
using ClinIQ.Application.Features.Scheduling.Queries;
using ClinIQ.Domain.Scheduling;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor")]
[Route("api/doctors/me/scheduling")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class DoctorSchedulingController(ISender sender) : ApiControllerBase
{
    [HttpPost("locations/{locationId:guid}/rules")]
    [ProducesResponseType<ScheduleRuleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRule(Guid locationId, ScheduleRuleRequest request, CancellationToken ct)
    { var r = await sender.Send(new CreateScheduleRuleCommand(locationId, request.Input()), ct); return r.Match<IActionResult>(row => CreatedAtAction(nameof(GetRule), new { id = row.Id }, row), Problem); }
    [HttpPut("rules/{id:guid}")]
    [ProducesResponseType<ScheduleRuleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRule(Guid id, ScheduleRuleRequest request, CancellationToken ct)
    { var r = await sender.Send(new UpdateScheduleRuleCommand(id, request.Input(), request.Version), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("rules/{id:guid}")]
    [ProducesResponseType<ScheduleRuleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct)
    { var r = await sender.Send(new GetScheduleRuleQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("rules")]
    [ProducesResponseType<SchedulePage<ScheduleRuleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rules(CancellationToken ct, Guid? locationId = null, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new ListScheduleRulesQuery(locationId, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("locations/{locationId:guid}/preview")]
    [ProducesResponseType<PublicationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(Guid locationId, PublicationRequest request, CancellationToken ct)
    { var r = await sender.Send(new PublishScheduleCommand(locationId, request.From!.Value, request.Until!.Value, true), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("locations/{locationId:guid}/publish")]
    [ProducesResponseType<PublicationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Publish(Guid locationId, PublicationRequest request, CancellationToken ct)
    { var r = await sender.Send(new PublishScheduleCommand(locationId, request.From!.Value, request.Until!.Value, false), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("locations/{locationId:guid}/extra-sessions")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Extra(Guid locationId, ExtraSessionRequest request, CancellationToken ct)
    { var r = await sender.Send(new ExtraSessionCommand(locationId, request.Date!.Value, request.Start!.Value, request.End!.Value, request.MaxCapacity!.Value, request.BookingCutoffMinutes!.Value), ct); return r.Match<IActionResult>(row => CreatedAtAction(nameof(GetSession), new { id = row.Id }, row), Problem); }
    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct)
    { var r = await sender.Send(new GetBookingSessionQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("sessions")]
    [ProducesResponseType<SchedulePage<SessionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions([FromQuery, Required] DateOnly? from, [FromQuery, Required] DateOnly? until, CancellationToken ct, Guid? locationId = null, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new ListBookingSessionsQuery(locationId, from!.Value, until!.Value, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPut("sessions/{id:guid}/times")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Times(Guid id, SessionTimesRequest request, CancellationToken ct) => Edit(new(id, SessionEditKind.Times, request.Version, request.Start!.Value, request.End!.Value), ct);
    [HttpPut("sessions/{id:guid}/capacity")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Capacity(Guid id, SessionValueRequest request, CancellationToken ct) => Edit(new(id, SessionEditKind.Capacity, request.Version, Value: request.Value!.Value), ct);
    [HttpPut("sessions/{id:guid}/cutoff")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Cutoff(Guid id, SessionValueRequest request, CancellationToken ct) => Edit(new(id, SessionEditKind.Cutoff, request.Version, Value: request.Value!.Value), ct);
    [HttpPost("sessions/{id:guid}/pause")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Pause(Guid id, SessionVersionRequest request, CancellationToken ct) => Edit(new(id, SessionEditKind.Pause, request.Version), ct);
    [HttpPost("sessions/{id:guid}/resume")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Resume(Guid id, SessionVersionRequest request, CancellationToken ct) => Edit(new(id, SessionEditKind.Resume, request.Version), ct);
    [HttpPost("sessions/{id:guid}/cancel")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, SessionReasonRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new ClinIQ.Application.Features.Appointments.Commands.CancelBookedSessionCommand(id, request.Version, request.Reason), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    private async Task<IActionResult> Edit(EditSessionCommand request, CancellationToken ct)
    { var r = await sender.Send(request, ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("exceptions")]
    [ProducesResponseType<ScheduleExceptionDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateException(ScheduleExceptionRequest request, CancellationToken ct)
    { var r = await sender.Send(new CreateScheduleExceptionCommand(request.LocationId, request.Date!.Value, request.Reason), ct); return r.Match<IActionResult>(row => CreatedAtAction(nameof(GetException), new { id = row.Id }, row), Problem); }
    [HttpGet("exceptions/{id:guid}")]
    [ProducesResponseType<ScheduleExceptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetException(Guid id, CancellationToken ct)
    { var r = await sender.Send(new GetScheduleExceptionQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("exceptions")]
    [ProducesResponseType<SchedulePage<ScheduleExceptionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Exceptions([FromQuery, Required] DateOnly? from, [FromQuery, Required] DateOnly? until, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new ListScheduleExceptionsQuery(from!.Value, until!.Value, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("exceptions/{id:guid}/remove")]
    [ProducesResponseType<ScheduleExceptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveException(Guid id, SessionReasonRequest request, CancellationToken ct)
    { var r = await sender.Send(new RemoveScheduleExceptionCommand(id, request.Version, request.Reason), ct); return r.Match<IActionResult>(Ok, Problem); }
}
public sealed class ScheduleRuleRequest
{
    [Required] public DayOfWeek? Day { get; init; }
    [Required] public TimeOnly? Start { get; init; }
    [Required] public TimeOnly? End { get; init; }
    [Required] public int? MaxCapacity { get; init; }
    [Required] public int? BookingCutoffMinutes { get; init; }
    [Required] public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveUntil { get; init; }
    public bool IsActive { get; init; } = true;
    public string Version { get; init; } = "";
    public ScheduleRuleData Input() => new(Day!.Value, Start!.Value, End!.Value, MaxCapacity!.Value, BookingCutoffMinutes!.Value, EffectiveFrom!.Value, EffectiveUntil, IsActive);
}
public sealed record PublicationRequest([Required] DateOnly? From, [Required] DateOnly? Until);
public sealed record ExtraSessionRequest([Required] DateOnly? Date, [Required] TimeOnly? Start, [Required] TimeOnly? End, [Required] int? MaxCapacity, [Required] int? BookingCutoffMinutes);
public sealed record SessionTimesRequest([Required] TimeOnly? Start, [Required] TimeOnly? End, string Version);
public sealed record SessionValueRequest([Required] int? Value, string Version);
public sealed record SessionVersionRequest(string Version);
public sealed record SessionReasonRequest(string Reason, string Version);
public sealed record ScheduleExceptionRequest([Required] DateOnly? Date, string Reason, Guid? LocationId = null);
