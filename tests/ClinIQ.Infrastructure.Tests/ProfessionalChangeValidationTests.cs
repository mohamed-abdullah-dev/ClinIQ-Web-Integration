using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Commands;
using ClinIQ.Application.Features.DoctorProfessionalChanges.Queries;
using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class ProfessionalChangeValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("AAAAAAAAAAAA")]
    public void MutationsRequireEightByteRowVersions(string? version)
    {
        var upload = new RegistrationUpload(DoctorEvidenceKind.Identity, "synthetic.pdf", "application/pdf", 1, Stream.Null);
        Assert.False(new SubmitProfessionalChangeCommandValidator().Validate(new SubmitProfessionalChangeCommand("Doctor", Guid.NewGuid(),
            ProfessionalGrade.GeneralPractitioner, version!, [upload])).IsValid);
        Assert.False(new ReviewProfessionalChangeCommandValidator().Validate(new ReviewProfessionalChangeCommand(Guid.NewGuid(), true, "Reason", version!)).IsValid);
    }
    [Fact]
    public void PaginationReasonAndDocumentBoundsAreEnforced()
    {
        Assert.False(new ListProfessionalChangesQueryValidator().Validate(new ListProfessionalChangesQuery(true, 0, 101, null)).IsValid);
        Assert.False(new ReviewProfessionalChangeCommandValidator().Validate(new ReviewProfessionalChangeCommand(Guid.NewGuid(), true, "", "AAAAAAAAAAA=")).IsValid);
        Assert.False(new SubmitProfessionalChangeCommandValidator().Validate(new SubmitProfessionalChangeCommand("Doctor", Guid.NewGuid(),
            ProfessionalGrade.GeneralPractitioner, "AAAAAAAAAAA=", [])).IsValid);
        Assert.True(new ReviewProfessionalChangeCommandValidator().Validate(new ReviewProfessionalChangeCommand(Guid.NewGuid(), false, "Reason", "AAAAAAAAAAA=")).IsValid);
    }
}
