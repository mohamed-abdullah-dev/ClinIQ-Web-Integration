using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorProfessionalChanges;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Commands;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Queries;
using ClinIQ.Domain.Doctors;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinIQ.API.Controllers;

[Authorize(Roles = "Doctor")]
[Route("api/doctors/me/professional-changes")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DoctorProfessionalChangesController(ISender sender) : ApiControllerBase
{
    [HttpGet("/api/doctors/me/professional-profile")]
    [ProducesResponseType<VerifiedDoctorProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var result = await sender.Send(new GetOwnVerifiedProfileQuery(), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet]
    [ProducesResponseType<ProfessionalChangePage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20, ProfessionalChangeStatus? status = null)
    {
        var result = await sender.Send(new ListProfessionalChangesQuery(false, page, pageSize, status), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProfessionalChangeDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetProfessionalChangeQuery(id, false), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(22 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 22 * 1024 * 1024)]
    [ProducesResponseType<ProfessionalChangeSummary>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Submit([FromForm] ProfessionalChangeUploadRequest request, CancellationToken ct)
    {
        var documents = new List<RegistrationUpload>();
        try
        {
            Add(request.PracticeLicense, DoctorEvidenceKind.PracticeLicense);
            Add(request.SyndicateRegistration, DoctorEvidenceKind.SyndicateRegistration);
            Add(request.IdentityEvidence, DoctorEvidenceKind.Identity);
            Add(request.ProfessionalGradeEvidence, DoctorEvidenceKind.ProfessionalGrade);
            var result = await sender.Send(new SubmitProfessionalChangeCommand(request.ProfessionalName.Trim(), request.SpecialtyId,
                request.ProfessionalGrade, request.ProfileVersion, documents), ct);
            return result.Match<IActionResult>(row => CreatedAtAction(nameof(Get), new { id = row.Id }, row), Problem);
        }
        finally { foreach (var document in documents) await document.Content.DisposeAsync(); }

        void Add(IFormFile? file, DoctorEvidenceKind kind)
        {
            if (file is not null) documents.Add(new(kind, file.FileName, file.ContentType, file.Length, file.OpenReadStream()));
        }
    }
    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var result = await sender.Send(new DownloadProfessionalChangeEvidenceQuery(id, documentId, false), ct);
        return result.Match<IActionResult>(document =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(document.Content, document.MediaType, document.FileName, enableRangeProcessing: false);
        }, Problem);
    }
}

public sealed class ProfessionalChangeUploadRequest
{
    public string ProfessionalName { get; init; } = "";
    public Guid SpecialtyId { get; init; }
    public ProfessionalGrade ProfessionalGrade { get; init; }
    public string ProfileVersion { get; init; } = "";
    public IFormFile? PracticeLicense { get; init; }
    public IFormFile? SyndicateRegistration { get; init; }
    public IFormFile? IdentityEvidence { get; init; }
    public IFormFile? ProfessionalGradeEvidence { get; init; }
}
