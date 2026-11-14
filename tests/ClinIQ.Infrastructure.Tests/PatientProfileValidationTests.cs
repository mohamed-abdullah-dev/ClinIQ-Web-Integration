using ClinIQ.Application.Features.Patients.Commands.SaveOwnProfile;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class PatientProfileValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAA")]
    public void MalformedVersionsAreRejected(string version)
    {
        var result = new SaveOwnPatientProfileCommandValidator().Validate(new SaveOwnPatientProfileCommand("Patient", "01012345678", version));
        Assert.Contains(result.Errors, error => error.PropertyName == "Version");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AAAAAAAAAAA=")]
    public void CreationAndSqlRowVersionInputsAreAccepted(string? version) =>
        Assert.True(new SaveOwnPatientProfileCommandValidator().Validate(new SaveOwnPatientProfileCommand("Patient", "01012345678", version)).IsValid);
}
