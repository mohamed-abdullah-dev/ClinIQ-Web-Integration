using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Application.Features.PremisesCorrections.Commands;
using ClinIQ.Application.Features.PremisesCorrections.Queries;
using ClinIQ.Domain.PremisesCorrections;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Admin")]
[Route("api/admin/premises-corrections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPremisesCorrectionsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PremisesCorrectionPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, Guid? locationId = null, Guid? branchId = null,
        PremisesCorrectionStatus? status = null, int page = 1, int pageSize = 20)
    {
        var r = await sender.Send(new ListPremisesCorrectionsQuery(true, locationId, branchId, status, page, pageSize), ct);
        return r.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var r = await sender.Send(new GetPremisesCorrectionQuery(id, true), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var r = await sender.Send(new DownloadCorrectionEvidenceQuery(id, documentId, true), ct);
        return r.Match<IActionResult>(file => { Response.Headers.XContentTypeOptions = "nosniff"; return File(file.Content, file.MediaType, file.FileName, enableRangeProcessing: false); }, Problem);
    }
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Approve(Guid id, PremisesCorrectionReviewRequest request, CancellationToken ct) => Review(id, PremisesCorrectionStatus.Approved, request, ct);
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Reject(Guid id, PremisesCorrectionReviewRequest request, CancellationToken ct) => Review(id, PremisesCorrectionStatus.Rejected, request, ct);
    [HttpPost("{id:guid}/request-information")]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> RequestInformation(Guid id, PremisesCorrectionReviewRequest request, CancellationToken ct) => Review(id, PremisesCorrectionStatus.NeedsInformation, request, ct);
    private async Task<IActionResult> Review(Guid id, PremisesCorrectionStatus outcome, PremisesCorrectionReviewRequest request, CancellationToken ct)
    {
        var r = await sender.Send(new ReviewPremisesCorrectionCommand(id, outcome, request.Reason, request.Version,
            request.SamePremisesVerified, request.LicenseVerified, request.AuthorityVerified), ct);
        return r.Match<IActionResult>(Ok, Problem);
    }
}
public sealed record PremisesCorrectionReviewRequest(string Reason, string Version, bool SamePremisesVerified = false,
    bool LicenseVerified = false, bool AuthorityVerified = false);
