using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Application.Features.AttendanceFeedback.Commands;
using ClinIQ.Application.Features.AttendanceFeedback.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "PatientMobile")]
[Route("api/patients/me/attendance-feedback")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class PatientAttendanceFeedbackController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<AttendancePage<FeedbackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackListQuery(page, pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<FeedbackDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var r = await sender.Send(new FeedbackGetQuery(id), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}/responses")]
    [ProducesResponseType<AttendancePage<FeedbackResponseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct, int page = 1, int pageSize = 20)
    { var r = await sender.Send(new FeedbackResponsesQuery(id, Page: page, PageSize: pageSize), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPost("{id:guid}/responses")]
    [ProducesResponseType<FeedbackAnswerReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(Guid id, FeedbackAnswerInput input, CancellationToken ct)
    { var r = await sender.Send(new AnswerFeedbackCommand(id, input), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpPut("{id:guid}/response")]
    [ProducesResponseType<FeedbackAnswerReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid id, FeedbackAnswerInput input, CancellationToken ct)
    { var r = await sender.Send(new AnswerFeedbackCommand(id, input, true), ct); return r.Match<IActionResult>(Ok, Problem); }
}
