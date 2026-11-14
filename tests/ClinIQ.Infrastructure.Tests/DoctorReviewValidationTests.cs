using ClinIQ.Application.Features.DoctorReviews.Commands;
using ClinIQ.Application.Features.DoctorReviews.Queries;
using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class DoctorReviewValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AQIDBA==")]
    public void InvalidVersionCannotSubmitDecision(string? version) =>
        Assert.False(new ReviewDoctorRegistrationCommandValidator().Validate(new ReviewDoctorRegistrationCommand(Guid.NewGuid(), true, "Reviewed", version!)).IsValid);
    [Fact]
    public void ValidVersionStillRequiresReasonAndId()
    {
        var version = Convert.ToBase64String(new byte[8]); var validator = new ReviewDoctorRegistrationCommandValidator();
        Assert.True(validator.Validate(new ReviewDoctorRegistrationCommand(Guid.NewGuid(), true, "Reviewed", version)).IsValid);
        Assert.False(validator.Validate(new ReviewDoctorRegistrationCommand(Guid.Empty, true, "", version)).IsValid);
        Assert.False(validator.Validate(new ReviewDoctorRegistrationCommand(Guid.NewGuid(), false, new string('x', 2001), version)).IsValid);
    }
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    [InlineData(1000001, 20)]
    public void PagingIsBounded(int page, int size) =>
        Assert.False(new ListDoctorRegistrationsQueryValidator().Validate(new ListDoctorRegistrationsQuery(page, size)).IsValid);
    [Fact]
    public void UnknownStatusAndEmptyResourceIdsAreInvalid()
    {
        Assert.False(new ListDoctorRegistrationsQueryValidator().Validate(new ListDoctorRegistrationsQuery(Status: (DoctorRegistrationStatus)999)).IsValid);
        Assert.False(new GetDoctorRegistrationQueryValidator().Validate(new GetDoctorRegistrationQuery(Guid.Empty)).IsValid);
        Assert.False(new DownloadDoctorEvidenceQueryValidator().Validate(new DownloadDoctorEvidenceQuery(Guid.Empty, Guid.Empty)).IsValid);
    }
}
