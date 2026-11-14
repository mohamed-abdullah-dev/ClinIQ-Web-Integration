using ClinIQ.Application.Features.DoctorProfessionalChanges;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Commands;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Queries;
using ClinIQ.Domain.Doctors;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Roles = "Admin")]
[Route("api/admin/doctor-professional-changes")]
[RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminProfessionalChangesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [EndpointDescription("Omitted status lists pending requests; an explicitly empty status includes all states.")]
    [ProducesResponseType<ProfessionalChangePage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20, ProfessionalChangeStatus? status = null)
    {
        var filter = Request.Query.ContainsKey(nameof(status)) ? status : ProfessionalChangeStatus.PendingReview;
        var result = await sender.Send(new ListProfessionalChangesQuery(true, page, pageSize, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProfessionalChangeDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetProfessionalChangeQuery(id, true), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<ProfessionalChangeSummary>(StatusCodes.Status200OK)]
    public Task<IActionResult> Approve(Guid id, ProfessionalChangeReviewRequest request, CancellationToken ct) => Review(id, true, request, ct);
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<ProfessionalChangeSummary>(StatusCodes.Status200OK)]
    public Task<IActionResult> Reject(Guid id, ProfessionalChangeReviewRequest request, CancellationToken ct) => Review(id, false, request, ct);
    private async Task<IActionResult> Review(Guid id, bool approve, ProfessionalChangeReviewRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new ReviewProfessionalChangeCommand(id, approve, request.Reason, request.Version), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var result = await sender.Send(new DownloadProfessionalChangeEvidenceQuery(id, documentId, true), ct);
        return result.Match<IActionResult>(document =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(document.Content, document.MediaType, document.FileName, enableRangeProcessing: false);
        }, Problem);
    }
}
public sealed record ProfessionalChangeReviewRequest(string Reason, string Version);
