using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorResubmissions;
using ClinIQ.Application.Features.DoctorResubmissions.Commands;
using ClinIQ.Application.Features.DoctorResubmissions.Queries;
using ClinIQ.Domain.Doctors;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinIQ.API.Controllers;

[Route("api/auth/doctors/resubmission")]
[AllowAnonymous]
[EnableRateLimiting("auth-sessions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DoctorResubmissionsController(ISender sender) : ApiControllerBase
{
    [HttpPost("link")]
    [EnableRateLimiting("auth-recovery")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestLink(RequestDoctorResubmissionLinkCommand request, CancellationToken ct)
    {
        var result = await sender.Send(request, ct);
        return result.Match<IActionResult>(_ => Accepted(), Problem);
    }

    [HttpGet]
    [ProducesResponseType<DoctorResubmissionPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get([FromHeader(Name = "X-Doctor-Resubmission")] string? token, CancellationToken ct)
    {
        var result = await sender.Send(new GetDoctorResubmissionQuery(token ?? ""), ct);
        return result.Match<IActionResult>(Ok, Problem);
    }

    [HttpGet("documents/{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid documentId, [FromHeader(Name = "X-Doctor-Resubmission")] string? token, CancellationToken ct)
    {
        var result = await sender.Send(new DownloadResubmissionEvidenceQuery(token ?? "", documentId), ct);
        return result.Match<IActionResult>(document => File(document.Content, document.MediaType, document.FileName), Problem);
    }

    [HttpPost]
    [EnableRateLimiting("auth-registration")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(22 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 22 * 1024 * 1024)]
    [ProducesResponseType<DoctorResubmissionReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit([FromForm] DoctorResubmissionRequest request,
        [FromHeader(Name = "X-Doctor-Resubmission")] string? token, CancellationToken ct)
    {
        var uploads = new List<RegistrationUpload>();
        try
        {
            Add(request.PracticeLicense, DoctorEvidenceKind.PracticeLicense);
            Add(request.SyndicateRegistration, DoctorEvidenceKind.SyndicateRegistration);
            Add(request.IdentityEvidence, DoctorEvidenceKind.Identity);
            Add(request.ProfessionalGradeEvidence, DoctorEvidenceKind.ProfessionalGrade);
            var result = await sender.Send(new ResubmitDoctorCommand(token ?? "", request.ProfessionalName.Trim(), request.PhoneNumber.Trim(),
                request.RegistrationNumber.Trim(), request.SpecialtyId, request.ProfessionalGrade, uploads), ct);
            return result.Match<IActionResult>(receipt => StatusCode(StatusCodes.Status201Created, receipt), Problem);
        }
        finally { foreach (var upload in uploads) await upload.Content.DisposeAsync(); }
        void Add(IFormFile? file, DoctorEvidenceKind kind)
        {
            if (file is not null) uploads.Add(new RegistrationUpload(kind, file.FileName, file.ContentType, file.Length, file.OpenReadStream()));
        }
    }
}

public sealed class DoctorResubmissionRequest
{
    public string ProfessionalName { get; init; } = "";
    public string PhoneNumber { get; init; } = "";
    public string RegistrationNumber { get; init; } = "";
    public Guid SpecialtyId { get; init; }
    public ProfessionalGrade ProfessionalGrade { get; init; }
    public IFormFile? PracticeLicense { get; init; }
    public IFormFile? SyndicateRegistration { get; init; }
    public IFormFile? IdentityEvidence { get; init; }
    public IFormFile? ProfessionalGradeEvidence { get; init; }
}
