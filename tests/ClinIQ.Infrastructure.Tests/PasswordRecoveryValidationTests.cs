using ClinIQ.Application.Features.Accounts.Commands.ForgotPassword;
using ClinIQ.Application.Features.Accounts.Commands.ResetPassword;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class PasswordRecoveryValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    public void ForgotRejectsInvalidEmail(string email) =>
        Assert.False(new ForgotPasswordCommandValidator().Validate(new ForgotPasswordCommand(email)).IsValid);

    [Fact]
    public void ForgotEnforcesEmailLength() => Assert.False(new ForgotPasswordCommandValidator()
        .Validate(new ForgotPasswordCommand(new string('a', 256) + "@example.test")).IsValid);

    [Fact]
    public void ResetRequiresAllProofAndPasswordFields()
    {
        var result = new ResetPasswordCommandValidator().Validate(new ResetPasswordCommand(Guid.Empty, "", ""));
        Assert.Contains(result.Errors, e => e.PropertyName == "UserId");
        Assert.Contains(result.Errors, e => e.PropertyName == "Token");
        Assert.Contains(result.Errors, e => e.PropertyName == "NewPassword");
    }
    [Fact]
    public void ResetBoundsProofAndPasswordSize()
    {
        var result = new ResetPasswordCommandValidator().Validate(new ResetPasswordCommand(Guid.NewGuid(), new string('t', 4097), new string('p', 129)));
        Assert.Contains(result.Errors, e => e.PropertyName == "Token");
        Assert.Contains(result.Errors, e => e.PropertyName == "NewPassword");
    }
    [Fact]
    public void ValidRequestShapesPassBoundaryValidation()
    {
        Assert.True(new ForgotPasswordCommandValidator().Validate(new ForgotPasswordCommand("synthetic@example.test")).IsValid);
        Assert.True(new ResetPasswordCommandValidator().Validate(new ResetPasswordCommand(Guid.NewGuid(), "encoded-proof", "Synthetic-New2!")).IsValid);
    }
}
