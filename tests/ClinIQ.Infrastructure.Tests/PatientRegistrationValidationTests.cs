using ClinIQ.Application.Features.Accounts.Commands.RegisterPatient;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class PatientRegistrationValidationTests
{
    [Theory]
    [InlineData("", "patient@example.test", "Synthetic-Test1!", "FullName")]
    [InlineData("   ", "patient@example.test", "Synthetic-Test1!", "FullName")]
    [InlineData("Synthetic Patient", "", "Synthetic-Test1!", "Email")]
    [InlineData("Synthetic Patient", "invalid", "Synthetic-Test1!", "Email")]
    [InlineData("Synthetic Patient", "patient@example.test", "", "Password")]
    public void InvalidInputIdentifiesItsField(string name, string email, string password, string field)
    {
        var result = new RegisterPatientCommandValidator().Validate(new RegisterPatientCommand(name, email, password));
        Assert.Contains(result.Errors, error => error.PropertyName == field);
    }

    [Fact]
    public void EnforcesInputLengthLimits()
    {
        var result = new RegisterPatientCommandValidator().Validate(new RegisterPatientCommand(
            new string('N', 201), new string('e', 250) + "@example.test", new string('P', 129)));
        Assert.Contains(result.Errors, error => error.PropertyName == "FullName");
        Assert.Contains(result.Errors, error => error.PropertyName == "Email");
        Assert.Contains(result.Errors, error => error.PropertyName == "Password");
    }

    [Fact]
    public void NameEmailPasswordAreSufficientForRegistration()
    {
        var result = new RegisterPatientCommandValidator().Validate(new RegisterPatientCommand(
            "Synthetic Patient", "patient@example.test", "Synthetic-Test1!"));
        Assert.True(result.IsValid);
    }
}
