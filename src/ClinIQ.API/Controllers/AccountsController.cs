using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.ConfirmEmail;
using ClinIQ.Application.Features.Accounts.Commands.RegisterDoctor;
using ClinIQ.Application.Features.Accounts.Commands.RegisterPatient;
using ClinIQ.Application.Features.Accounts.Commands.ResendConfirmation;
using ClinIQ.Domain.Doctors;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinIQ.API.Controllers;

[Route("api/auth")]
[EnableRateLimiting("auth-registration")]
public sealed class AccountsController(ISender sender) : ApiControllerBase
{
    [HttpPost("patients/register")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<PatientRegistrationReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RegisterPatient([FromBody] PatientRegistrationRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RegisterPatientCommand(request.FullName.Trim(), request.Email.Trim(), request.Password),
            cancellationToken);
        return result.Match<IActionResult>(receipt => StatusCode(StatusCodes.Status201Created, receipt), Problem);
    }

    [HttpPost("doctors/register")]
    [AllowAnonymous]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(22 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 22 * 1024 * 1024)]
    [ProducesResponseType<DoctorRegistrationReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RegisterDoctor([FromForm] DoctorRegistrationRequest request, CancellationToken cancellationToken)
    {
        var uploads = new List<RegistrationUpload>();
        Add(request.PracticeLicense, DoctorEvidenceKind.PracticeLicense);
        Add(request.SyndicateRegistration, DoctorEvidenceKind.SyndicateRegistration);
        Add(request.IdentityEvidence, DoctorEvidenceKind.Identity);
        Add(request.ProfessionalGradeEvidence, DoctorEvidenceKind.ProfessionalGrade);
        try
        {
            var result = await sender.Send(new RegisterDoctorCommand(request.FullName.Trim(), request.Email.Trim(), request.Password,
                request.PhoneNumber.Trim(), request.RegistrationNumber.Trim(), request.SpecialtyId, request.ProfessionalGrade, uploads),
                cancellationToken);
            return result.Match<IActionResult>(receipt => StatusCode(StatusCodes.Status201Created, receipt), Problem);
        }
        finally
        {
            foreach (var upload in uploads) await upload.Content.DisposeAsync();
        }

        void Add(IFormFile? file, DoctorEvidenceKind kind)
        {
            if (file is not null)
                uploads.Add(new RegistrationUpload(kind, file.FileName, file.ContentType, file.Length, file.OpenReadStream()));
        }
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailCommand request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(request, cancellationToken);
        return result.Match<IActionResult>(_ => NoContent(), Problem);
    }

    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationCommand request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(request, cancellationToken);
        return result.Match<IActionResult>(_ => Accepted(), Problem);
    }
}

public sealed class DoctorRegistrationRequest
{
    public string FullName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Password { get; init; } = "";
    public string PhoneNumber { get; init; } = "";
    public string RegistrationNumber { get; init; } = "";
    public Guid SpecialtyId { get; init; }
    public ProfessionalGrade ProfessionalGrade { get; init; }
    public IFormFile? PracticeLicense { get; init; }
    public IFormFile? SyndicateRegistration { get; init; }
    public IFormFile? IdentityEvidence { get; init; }
    public IFormFile? ProfessionalGradeEvidence { get; init; }
}

public sealed record PatientRegistrationRequest(string FullName, string Email, string Password);
