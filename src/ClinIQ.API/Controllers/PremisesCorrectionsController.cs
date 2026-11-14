using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Application.Features.PremisesCorrections.Commands;
using ClinIQ.Application.Features.PremisesCorrections.Queries;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Policy = "ClinicWeb", Roles = "Doctor,ClinicManager,ClinicApplicant")]
[Route("api/premises-corrections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PremisesCorrectionsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PremisesCorrectionPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, Guid? locationId = null, Guid? branchId = null,
        PremisesCorrectionStatus? status = null, int page = 1, int pageSize = 20)
    {
        var r = await sender.Send(new ListPremisesCorrectionsQuery(false, locationId, branchId, status, page, pageSize), ct);
        return r.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { var r = await sender.Send(new GetPremisesCorrectionQuery(id, false), ct); return r.Match<IActionResult>(Ok, Problem); }

    [HttpPost("locations/{targetId:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status201Created)]
    public Task<IActionResult> SubmitLocation(Guid targetId, [FromForm] PremisesCorrectionSubmissionRequest request, CancellationToken ct) => Submit(targetId, false, request, ct);

    [HttpPost("branches/{targetId:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
    [ProducesResponseType<PremisesCorrectionDto>(StatusCodes.Status201Created)]
    public Task<IActionResult> SubmitBranch(Guid targetId, [FromForm] PremisesCorrectionSubmissionRequest request, CancellationToken ct) => Submit(targetId, true, request, ct);

    private async Task<IActionResult> Submit(Guid targetId, bool branch, PremisesCorrectionSubmissionRequest request, CancellationToken ct)
    {
        var documents = new List<PracticeLocationUpload>();
        try
        {
            Add(request.FacilityLicense, PracticeLocationEvidenceKind.FacilityLicense);
            Add(request.WorkAuthorization, PracticeLocationEvidenceKind.WorkAuthorization);
            var proposed = new PremisesDetails(request.Name, request.Address, request.Latitude, request.Longitude,
                request.PublicPhone, request.LicensingAuthority, request.LicenseNumber);
            var r = await sender.Send(new SubmitPremisesCorrectionCommand(targetId, branch, proposed, request.Reason, request.TargetVersion, documents), ct);
            return r.Match<IActionResult>(row => CreatedAtAction(nameof(Get), new { id = row.Id }, row), Problem);
        }
        finally { foreach (var document in documents) await document.Content.DisposeAsync(); }
        void Add(IFormFile? file, PracticeLocationEvidenceKind kind)
        { if (file is not null) documents.Add(new PracticeLocationUpload(kind, file.FileName, file.ContentType, file.Length, file.OpenReadStream())); }
    }
    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var r = await sender.Send(new DownloadCorrectionEvidenceQuery(id, documentId, false), ct);
        return r.Match<IActionResult>(file => { Response.Headers.XContentTypeOptions = "nosniff"; return File(file.Content, file.MediaType, file.FileName, enableRangeProcessing: false); }, Problem);
    }
}
public sealed class PremisesCorrectionSubmissionRequest
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public string? PublicPhone { get; init; }
    public string? LicensingAuthority { get; init; }
    public string? LicenseNumber { get; init; }
    public string Reason { get; init; } = "";
    public string TargetVersion { get; init; } = "";
    public IFormFile? FacilityLicense { get; init; }
    public IFormFile? WorkAuthorization { get; init; }
}
