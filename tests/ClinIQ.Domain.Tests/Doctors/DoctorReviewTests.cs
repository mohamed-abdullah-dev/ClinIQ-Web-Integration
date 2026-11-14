using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Doctors;

public sealed class DoctorReviewTests
{
    [Theory]
    [InlineData(true, DoctorRegistrationStatus.Approved)]
    [InlineData(false, DoctorRegistrationStatus.Rejected)]
    public void ReviewRecordsTrustedActorTimeReasonAndFinalOutcome(bool approve, DoctorRegistrationStatus expected)
    {
        var registration = Registration(); var reviewer = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var result = registration.Review(approve, " Reviewed synthetic evidence ", reviewer, now);
        Assert.True(result.IsSuccess); Assert.Equal(expected, registration.Status);
        Assert.Equal(registration.Id, result.Value.DoctorRegistrationId);
        Assert.Equal(reviewer, result.Value.ReviewerUserId); Assert.Equal(now, result.Value.ReviewedAtUtc);
        Assert.Equal("Reviewed synthetic evidence", result.Value.Reason); Assert.Equal(expected, result.Value.Outcome);
        Assert.Equal(DoctorReviewErrors.AlreadyReviewed, Assert.Single(registration.Review(!approve, "Other reason", reviewer, now).Errors));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingReasonLeavesRequestPending(string? reason)
    {
        var registration = Registration();
        Assert.Equal(DoctorReviewErrors.ReasonRequired, Assert.Single(registration.Review(true, reason, Guid.NewGuid(), DateTimeOffset.UtcNow).Errors));
        Assert.Equal(DoctorRegistrationStatus.PendingReview, registration.Status);
    }
    [Fact]
    public void ExcessiveReasonAndSelfReviewCannotApprove()
    {
        var registration = Registration();
        Assert.True(registration.Review(true, new string('x', 2001), Guid.NewGuid(), DateTimeOffset.UtcNow).IsError);
        Assert.Equal(DoctorReviewErrors.SelfReview, Assert.Single(registration.Review(true, "Reviewed", registration.UserId, DateTimeOffset.UtcNow).Errors));
        Assert.Equal(DoctorRegistrationStatus.PendingReview, registration.Status);
    }
    [Fact]
    public void ProfileCopiesOnlyApprovedProfessionalData()
    {
        var registration = Registration();
        Assert.True(DoctorProfile.FromApprovedRegistration(registration).IsError);
        Assert.True(registration.Review(true, "Reviewed", Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);
        var profile = DoctorProfile.FromApprovedRegistration(registration).Value;
        Assert.Equal(registration.Id, profile.DoctorRegistrationId); Assert.Equal(registration.UserId, profile.UserId);
        Assert.Equal(registration.SpecialtyId, profile.SpecialtyId); Assert.Equal(registration.ProfessionalGrade, profile.ProfessionalGrade);
        Assert.Equal(registration.ProfessionalName, profile.ProfessionalName);
        var rejected = Registration(); Assert.True(rejected.Review(false, "Rejected", Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);
        Assert.True(DoctorProfile.FromApprovedRegistration(rejected).IsError);
    }
    private static DoctorRegistration Registration() => DoctorRegistration.Create(Guid.NewGuid(), Guid.NewGuid(), "Synthetic Doctor", "01012345678", "SYNTHETIC",
        Guid.NewGuid(), ProfessionalGrade.GeneralPractitioner,
        new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
            .Select(kind => new DoctorEvidenceMetadata(kind, Guid.NewGuid().ToString("N"), "synthetic.pdf", "application/pdf", 5, new string('A', 64))).ToArray()).Value;
}
