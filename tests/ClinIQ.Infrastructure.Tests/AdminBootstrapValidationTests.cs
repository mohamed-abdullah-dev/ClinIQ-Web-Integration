using ClinIQ.Application.Features.Accounts;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class AdminBootstrapValidationTests
{
    [Theory]
    [InlineData("", "admin@example.test", "Synthetic-Admin1!")]
    [InlineData("Admin", "invalid", "Synthetic-Admin1!")]
    [InlineData("Admin", "admin@example.test", "")]
    public void RequiredAndEmailRulesRejectInvalidInput(string name, string email, string password) =>
        Assert.False(new AdminBootstrapInputValidator().Validate(new AdminBootstrapInput(name, email, password)).IsValid);

    [Fact]
    public void BoundsAreEnforced()
    {
        var validator = new AdminBootstrapInputValidator();
        Assert.False(validator.Validate(new AdminBootstrapInput(new string('n', 201), "admin@example.test", "Synthetic-Admin1!")).IsValid);
        Assert.False(validator.Validate(new AdminBootstrapInput("Admin", new string('e', 244) + "@example.test", "Synthetic-Admin1!")).IsValid);
        Assert.False(validator.Validate(new AdminBootstrapInput("Admin", "admin@example.test", new string('P', 129))).IsValid);
    }

    [Fact]
    public void InputHasNoCredentialBearingStringRepresentation()
    {
        var input = new AdminBootstrapInput("Synthetic Admin", "admin@example.test", "Synthetic-Admin1!");
        Assert.True(new AdminBootstrapInputValidator().Validate(input).IsValid);
        Assert.DoesNotContain(input.Password, input.ToString());
        Assert.DoesNotContain(input.Email, input.ToString());
    }
}
