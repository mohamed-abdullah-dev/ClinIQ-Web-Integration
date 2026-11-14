using ClinIQ.Domain.Patients;
using Xunit;

namespace ClinIQ.Domain.Tests.Patients;

public sealed class PatientProfileTests
{
    [Fact]
    public void ContactUpdatesKeepTheAccountAndProfileIdentity()
    {
        var userId = Guid.NewGuid();
        var profile = PatientProfile.Create(userId, "  اسم المريض  ", " +201012345678 ").Value;
        var profileId = profile.Id;
        Assert.Equal("اسم المريض", profile.FullName);
        Assert.Equal("+201012345678", profile.PhoneNumber);
        Assert.True(profile.UpdateContact("Updated name", "01012345678").IsSuccess);
        Assert.Equal(profileId, profile.Id);
        Assert.Equal(userId, profile.UserId);
    }

    [Theory]
    [InlineData("", "01012345678")]
    [InlineData("   ", "01012345678")]
    [InlineData("Patient", "")]
    [InlineData("Patient", "+123456")]
    [InlineData("Patient", "1234567890123456")]
    [InlineData("Patient", "010-12345678")]
    [InlineData("Patient", "٠١٠١٢٣٤٥٦٧٨")]
    [InlineData("Patient", "++201012345678")]
    public void InvalidContactDoesNotPartiallyChangeAnExistingProfile(string name, string phone)
    {
        var profile = PatientProfile.Create(Guid.NewGuid(), "Original", "01012345678").Value;
        Assert.True(profile.UpdateContact(name, phone).IsError);
        Assert.Equal("Original", profile.FullName);
        Assert.Equal("01012345678", profile.PhoneNumber);
    }

    [Fact]
    public void NamesAreBoundedAndAnAccountIsRequired()
    {
        Assert.True(PatientProfile.Create(Guid.NewGuid(), new string('x', 201), "01012345678").IsError);
        Assert.Throws<ArgumentException>(() => PatientProfile.Create(Guid.Empty, "Patient", "01012345678"));
    }

    [Fact]
    public void SharedContactPhoneDoesNotMergeDifferentPatients()
    {
        var first = PatientProfile.Create(Guid.NewGuid(), "First", "01012345678").Value;
        var second = PatientProfile.Create(Guid.NewGuid(), "Second", first.PhoneNumber).Value;
        Assert.NotEqual(first.UserId, second.UserId);
        Assert.NotEqual(first.Id, second.Id);
    }
}
