using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Application.Features.DoctorReviews.Commands;
using ClinIQ.Application.Features.DoctorReviews.Queries;
using ClinIQ.Domain.Doctors;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Route("api/admin/doctor-registrations")]
[Authorize(Roles = "Admin")]
[RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminDoctorRegistrationsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [EndpointDescription("Lists pending registrations when status is omitted. An explicitly empty status includes all states.")]
    [ProducesResponseType<DoctorRegistrationPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] DoctorRegistrationStatus? status = null)
    {
        // ApiExplorer represents a non-null enum parameter default as its underlying integer.
        // Apply our default here so OpenAPI never serializes that integer as a nullable enum.
        var filter = Request.Query.ContainsKey(nameof(status)) ? status : DoctorRegistrationStatus.PendingReview;
        var result = await sender.Send(new ListDoctorRegistrationsQuery(page, pageSize, filter), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DoctorRegistrationDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetDoctorRegistrationQuery(id), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var result = await sender.Send(new DownloadDoctorEvidenceQuery(id, documentId), ct);
        return result.Match<IActionResult>(document =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(document.Content, document.MediaType, document.FileName, enableRangeProcessing: false);
        }, Problem);
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<DoctorReviewReceipt>(StatusCodes.Status200OK)]
    public Task<IActionResult> Approve(Guid id, ReviewDoctorRequest request, CancellationToken ct) => Review(id, true, request, ct);

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<DoctorReviewReceipt>(StatusCodes.Status200OK)]
    public Task<IActionResult> Reject(Guid id, ReviewDoctorRequest request, CancellationToken ct) => Review(id, false, request, ct);

    private async Task<IActionResult> Review(Guid id, bool approve, ReviewDoctorRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new ReviewDoctorRegistrationCommand(id, approve, request.Reason, request.Version), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
}

public sealed record ReviewDoctorRequest(string Reason, string Version);
