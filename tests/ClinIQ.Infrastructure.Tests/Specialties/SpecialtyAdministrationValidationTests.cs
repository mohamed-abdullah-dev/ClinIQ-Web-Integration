using ClinIQ.Application.Features.Specialties.Commands.CreateSpecialty;
using ClinIQ.Application.Features.Specialties.Commands.DeleteSpecialty;
using ClinIQ.Application.Features.Specialties.Commands.RenameSpecialty;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Specialties;

public sealed class SpecialtyAdministrationValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AAAAAAAAAAAA")]
    public void UpdateAndDeleteRequireAnEightByteSqlVersion(string? version)
    {
        var id = Guid.NewGuid();
        Assert.False(new RenameSpecialtyCommandValidator().Validate(new RenameSpecialtyCommand(id, "جراحة", "Surgery", version!)).IsValid);
        Assert.False(new DeleteSpecialtyCommandValidator().Validate(new DeleteSpecialtyCommand(id, version!)).IsValid);
    }
    [Fact]
    public void ValidNamesAndVersionsPassButEmptyIdentityAndLongNamesDoNot()
    {
        Assert.True(new CreateSpecialtyCommandValidator().Validate(new CreateSpecialtyCommand("جراحة", "Surgery")).IsValid);
        Assert.True(new RenameSpecialtyCommandValidator().Validate(new RenameSpecialtyCommand(Guid.NewGuid(), "جراحة", "Surgery", "AAAAAAAAAAA=")).IsValid);
        Assert.False(new DeleteSpecialtyCommandValidator().Validate(new DeleteSpecialtyCommand(Guid.Empty, "AAAAAAAAAAA=")).IsValid);
        Assert.False(new CreateSpecialtyCommandValidator().Validate(new CreateSpecialtyCommand("جراحة", new string('a', 201))).IsValid);
    }
}
