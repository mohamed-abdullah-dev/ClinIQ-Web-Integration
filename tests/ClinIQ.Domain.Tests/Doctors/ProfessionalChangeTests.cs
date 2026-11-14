using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Doctors;

public sealed class ProfessionalChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static DoctorEvidenceMetadata[] Evidence(params DoctorEvidenceKind[] kinds) => kinds.Select(kind =>
        new DoctorEvidenceMetadata(kind, Guid.NewGuid().ToString("N"), "synthetic.pdf", "application/pdf", 20, new string('A', 64))).ToArray();
    internal static DoctorProfile Profile()
    {
        var registration = DoctorRegistration.Create(Guid.NewGuid(), Guid.NewGuid(), "Original doctor", "01012345678", "SYNTHETIC", Guid.NewGuid(),
            ProfessionalGrade.GeneralPractitioner, Evidence(DoctorEvidenceKind.Identity, DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration)).Value;
        Assert.True(registration.Review(true, "Approved", Guid.NewGuid(), Now).IsSuccess);
        var profile = DoctorProfile.FromApprovedRegistration(registration).Value;
        typeof(DoctorProfile).GetProperty(nameof(DoctorProfile.Version))!.SetValue(profile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return profile;
    }
    [Fact]
    public void SubmissionDoesNotMutateTheApprovedProfileAndCopiesItsBaseline()
    {
        var profile = Profile(); var originalId = profile.Id;
        var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "  Updated doctor  ", profile.SpecialtyId,
            profile.ProfessionalGrade, Evidence(DoctorEvidenceKind.Identity)).Value;
        Assert.Equal("Original doctor", profile.ProfessionalName); Assert.Equal(originalId, profile.Id);
        Assert.Equal("Updated doctor", request.ProfessionalName); Assert.Equal("Original doctor", request.OriginalProfessionalName);
        Assert.Equal(ProfessionalChangeStatus.PendingReview, request.Status); Assert.Null(request.ReviewerUserId);
        Assert.NotSame(profile.Version, request.OriginalProfileVersion); Assert.Equal(profile.Version, request.OriginalProfileVersion);
    }
    [Theory]
    [InlineData("name", DoctorEvidenceKind.PracticeLicense)]
    [InlineData("specialty", DoctorEvidenceKind.Identity)]
    [InlineData("grade", DoctorEvidenceKind.Identity)]
    public void EachChangedFieldRequiresItsSupportingEvidence(string field, DoctorEvidenceKind wrongKind)
    {
        var profile = Profile();
        var result = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, field == "name" ? "New name" : profile.ProfessionalName,
            field == "specialty" ? Guid.NewGuid() : profile.SpecialtyId,
            field == "grade" ? ProfessionalGrade.Consultant : profile.ProfessionalGrade, Evidence(wrongKind));
        Assert.Equal("ProfessionalChange.RequiredEvidence", result.Errors.Single().Code);
    }
    [Fact]
    public void NoChangesAndDuplicateOrInvalidDocumentsAreRejected()
    {
        var profile = Profile();
        Assert.Equal("ProfessionalChange.NoChanges", DoctorProfessionalChange.Create(Guid.NewGuid(), profile, profile.ProfessionalName,
            profile.SpecialtyId, profile.ProfessionalGrade, Evidence(DoctorEvidenceKind.Identity)).Errors.Single().Code);
        Assert.True(DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "New", profile.SpecialtyId, profile.ProfessionalGrade,
            Evidence(DoctorEvidenceKind.Identity, DoctorEvidenceKind.Identity)).IsError);
        Assert.True(DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "", profile.SpecialtyId, profile.ProfessionalGrade,
            Evidence(DoctorEvidenceKind.Identity)).IsError);
    }
    [Fact]
    public void ApprovalPublishesExactlyTheProposedFieldsAndPreservesIdentityAndOriginalSnapshot()
    {
        var profile = Profile(); var profileId = profile.Id; var userId = profile.UserId; var registrationId = profile.DoctorRegistrationId;
        var specialty = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "New name", specialty, ProfessionalGrade.Consultant,
            Evidence(DoctorEvidenceKind.Identity, DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.ProfessionalGrade)).Value;
        Assert.True(request.Review(profile, true, "  Evidence reviewed  ", reviewer, Now).IsSuccess);
        Assert.Equal("New name", profile.ProfessionalName); Assert.Equal(specialty, profile.SpecialtyId); Assert.Equal(ProfessionalGrade.Consultant, profile.ProfessionalGrade);
        Assert.Equal(profileId, profile.Id); Assert.Equal(userId, profile.UserId); Assert.Equal(registrationId, profile.DoctorRegistrationId);
        Assert.Equal("Original doctor", request.OriginalProfessionalName); Assert.Equal(reviewer, request.ReviewerUserId);
        Assert.Equal("Evidence reviewed", request.ReviewReason); Assert.Equal(Now, request.ReviewedAtUtc);
        Assert.Equal(ProfessionalChangeStatus.Approved, request.Status);
        Assert.Equal("ProfessionalChange.AlreadyReviewed", request.Review(profile, false, "Late", Guid.NewGuid(), Now).Errors.Single().Code);
    }
    [Fact]
    public void RejectionPreservesTheApprovedProfileAndRecordsItsReason()
    {
        var profile = Profile(); var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "New name", profile.SpecialtyId,
            profile.ProfessionalGrade, Evidence(DoctorEvidenceKind.Identity)).Value;
        Assert.True(request.Review(profile, false, "Not supported", Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal("Original doctor", profile.ProfessionalName); Assert.Equal(ProfessionalChangeStatus.Rejected, request.Status);
        Assert.Equal("Not supported", request.ReviewReason);
    }
    [Fact]
    public void SelfReviewAndMissingReasonDoNotPublishOrFinalize()
    {
        var profile = Profile(); var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "New name", profile.SpecialtyId,
            profile.ProfessionalGrade, Evidence(DoctorEvidenceKind.Identity)).Value;
        Assert.Equal("ProfessionalChange.SelfReview", request.Review(profile, true, "Reason", profile.UserId, Now).Errors.Single().Code);
        Assert.True(request.Review(profile, true, " ", Guid.NewGuid(), Now).IsError);
        Assert.Equal("Original doctor", profile.ProfessionalName); Assert.Equal(ProfessionalChangeStatus.PendingReview, request.Status);
    }
    [Fact]
    public void AStaleBaselineCannotPublishButCanBeRejectedToPermitANewSubmission()
    {
        var profile = Profile(); var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "New name", profile.SpecialtyId,
            profile.ProfessionalGrade, Evidence(DoctorEvidenceKind.Identity)).Value;
        typeof(DoctorProfile).GetProperty(nameof(DoctorProfile.Version))!.SetValue(profile, new byte[8]);
        Assert.Equal("ProfessionalChange.StaleProfile", request.Review(profile, true, "Reason", Guid.NewGuid(), Now).Errors.Single().Code);
        Assert.Equal(ProfessionalChangeStatus.PendingReview, request.Status); Assert.Equal("Original doctor", profile.ProfessionalName);
        Assert.True(request.Review(profile, false, "Outdated baseline", Guid.NewGuid(), Now).IsSuccess);
    }
}
