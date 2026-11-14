using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Application.Features.AttendanceFeedback.Commands;
using ClinIQ.Application.Features.AttendanceFeedback.Queries;
using ClinIQ.Domain.AttendanceFeedback;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Admin")]
[Route("api/admin/attendance-feedback")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class AdminAttendanceFeedbackController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<AttendancePage<FeedbackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, AttendanceReviewState state = AttendanceReviewState.Open, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackReviewsQuery(state, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<FeedbackReviewDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var r = await sender.Send(new FeedbackReviewQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}/responses")]
    [ProducesResponseType<AttendancePage<FeedbackResponseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Responses(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackResponsesQuery(id, true, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}/staff-history")]
    [ProducesResponseType<AttendancePage<AttendanceHistory>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StaffHistory(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackStaffHistoryQuery(id, page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/finalize")]
    [ProducesResponseType<FeedbackReviewDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finalize(Guid id, FeedbackReviewInput input, CancellationToken ct)
    { var r = await sender.Send(new FinalizeFeedbackReviewCommand(id, input), ct); return r.Match<IActionResult>(Ok, Problem); }
}
