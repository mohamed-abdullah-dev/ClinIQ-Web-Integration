using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;
using Xunit;

namespace ClinIQ.Domain.Tests.PremisesCorrections;

public sealed class PremisesCorrectionTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ApprovalRequiresAllChecksIncludingSamePhysicalPremises(bool same, bool license, bool authority)
    {
        var row = Request();
        Assert.Equal(PremisesCorrectionErrors.Verification, Assert.Single(row.Review(PremisesCorrectionStatus.Approved, "Verified", Guid.NewGuid(), DateTimeOffset.UnixEpoch, same, license, authority).Errors));
        Assert.Equal(PremisesCorrectionStatus.PendingReview, row.Status); Assert.Null(row.ReviewedAtUtc);
    }
    [Theory]
    [InlineData(PremisesCorrectionStatus.Approved)]
    [InlineData(PremisesCorrectionStatus.Rejected)]
    [InlineData(PremisesCorrectionStatus.NeedsInformation)]
    public void DecisionIsFinalAndSnapshotsRemainImmutable(PremisesCorrectionStatus outcome)
    {
        var row = Request();
        Assert.True(row.Review(outcome, " Decision reason ", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, true).IsSuccess);
        Assert.Equal("Decision reason", row.ReviewReason); Assert.Equal("Address", row.Original.Address); Assert.Equal("Corrected address", row.Proposed.Address);
        Assert.Equal(PremisesCorrectionErrors.AlreadyReviewed, Assert.Single(row.Review(PremisesCorrectionStatus.Approved, "Again", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, true).Errors));
    }
    [Fact]
    public void SelfReviewAndMissingReasonAreRejected()
    {
        var row = Request();
        Assert.Equal(PremisesCorrectionErrors.SelfReview, Assert.Single(row.Review(PremisesCorrectionStatus.Approved, "Own", row.ApplicantUserId, DateTimeOffset.UnixEpoch, true, true, true).Errors));
        Assert.Equal(PremisesCorrectionErrors.Reason, Assert.Single(row.Review(PremisesCorrectionStatus.Rejected, " ", Guid.NewGuid(), DateTimeOffset.UnixEpoch, false, false, false).Errors));
    }
    [Fact]
    public void NoOpAndMissingLicenseDoNotCreateRequests()
    {
        var original = new PremisesDetails("Name", "Address", null, null);
        Assert.Equal(PremisesCorrectionErrors.NoChanges, Assert.Single(PremisesCorrection.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), original, new byte[8], original with { Name = " Name " }, "Reason", Evidence()).Errors));
        Assert.Equal(PremisesCorrectionErrors.Evidence, Assert.Single(PremisesCorrection.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), original, new byte[8], original with { Address = "Correction" }, "Reason", []).Errors));
    }
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("abc", "Authority", "License")]
    [InlineData("01012345678", " ", "License")]
    public void BranchDetailsRequireContactAndLicense(string? phone, string? authority, string? license) =>
        Assert.True(new PremisesDetails("Name", "Address", null, null, phone, authority, license).Normalize(true).IsError);
    [Fact]
    public void IndependentDetailsCannotSmuggleBranchLicenseOrContact() =>
        Assert.True(new PremisesDetails("Name", "Address", null, null, "01012345678").Normalize(false).IsError);
    private static PremisesCorrection Request() => PremisesCorrection.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
        new("Name", "Address", null, null), new byte[8], new("Name", "Corrected address", null, null), "Typo", Evidence()).Value;
    private static PracticeLocationEvidenceMetadata[] Evidence() =>
        [new(PracticeLocationEvidenceKind.FacilityLicense, "test/doc.pdf", "doc.pdf", "application/pdf", 10, new string('A', 64))];
}
