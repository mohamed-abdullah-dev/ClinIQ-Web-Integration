using ClinIQ.Domain.PracticeLocations;
using Xunit;

namespace ClinIQ.Domain.Tests.PracticeLocations;

public sealed class DoctorPracticeLocationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("150.50")]
    [InlineData("9999999999999999.99")]
    public void EgpFeesIncludeZeroWithoutRounding(string value)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); var price = ConsultationPrice.Create(amount, " egp ");
        Assert.True(price.IsSuccess); Assert.Equal(amount, price.Value.Amount); Assert.Equal("EGP", price.Value.Currency);
    }
    [Theory]
    [InlineData("-0.01", "EGP")]
    [InlineData("1.001", "EGP")]
    [InlineData("10000000000000000", "EGP")]
    [InlineData("10", "USD")]
    [InlineData("10", "")]
    [InlineData("10", null)]
    public void InvalidFeesAndOtherCurrenciesFail(string value, string? currency) =>
        Assert.True(ConsultationPrice.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), currency).IsError);

    [Fact]
    public void DraftSubmissionNeedsLicenseAndLocksPhysicalFieldsWhilePending()
    {
        var row = Location(); Assert.Equal(PracticeLocationStatus.Draft, row.Status);
        Assert.Equal(PracticeLocationErrors.RequiredEvidence, Assert.Single(row.Submit(Guid.NewGuid(), row.UserId, []).Errors));
        var submission = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        Assert.Equal(PracticeLocationStatus.PendingReview, row.Status); Assert.Equal(row.Id, submission.LocationId);
        Assert.Equal(submission.Id, row.CurrentSubmissionId); Assert.Equal(1, row.AttemptNumber);
        Assert.Equal(PracticeLocationErrors.DraftLocked, Assert.Single(row.UpdateDraft(row.UserId, Details("Other premises"), Price(50)).Errors));
        Assert.Equal(PracticeLocationErrors.DraftLocked, Assert.Single(row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Errors));
        Assert.Equal(PracticeLocationErrors.NotApproved, Assert.Single(row.ChangeFee(row.UserId, Price(50)).Errors));
    }
    [Theory]
    [InlineData(PracticeLocationStatus.NeedsInformation)]
    [InlineData(PracticeLocationStatus.Rejected)]
    public void CorrectionCreatesNewSnapshotAndRetainsOldDecisionAndEvidence(PracticeLocationStatus outcome)
    {
        var row = Location(); var first = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        var doc = Assert.Single(first.Documents); var reviewer = Guid.NewGuid();
        Assert.True(row.Review(first, outcome, "  Need a matching license  ", reviewer, DateTimeOffset.UnixEpoch, false, false).IsSuccess);
        Assert.True(row.UpdateDraft(row.UserId, Details("Corrected address"), Price(60)).IsSuccess);
        var next = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        Assert.Equal(2, next.AttemptNumber); Assert.Equal(outcome, first.Status); Assert.Equal("Address", first.Address);
        Assert.Equal("Need a matching license", first.ReviewReason); Assert.Equal(reviewer, first.ReviewerUserId);
        Assert.Equal(doc.Id, Assert.Single(first.Documents).Id); Assert.NotEqual(doc.Id, Assert.Single(next.Documents).Id);
        Assert.Equal("Corrected address", next.Address); Assert.Equal(PracticeLocationStatus.PendingReview, next.Status);
        Assert.Equal(PracticeLocationErrors.AlreadyReviewed, Assert.Single(row.Review(first, PracticeLocationStatus.Approved, "Late", reviewer, DateTimeOffset.UnixEpoch, true, true).Errors));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ApprovalRequiresBothManualChecks(bool license, bool work)
    {
        var row = Location(); var submission = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        Assert.Equal(PracticeLocationErrors.UnverifiedEvidence, Assert.Single(row.Review(submission, PracticeLocationStatus.Approved, "Check", Guid.NewGuid(), DateTimeOffset.UnixEpoch, license, work).Errors));
        Assert.Equal(PracticeLocationStatus.PendingReview, row.Status); Assert.Null(submission.ReviewerUserId);
    }
    [Fact]
    public void ApprovalAllowsOnlyOwnerFeeChangeAndKeepsTheReviewedSnapshot()
    {
        var row = Location(); var submission = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        Assert.True(row.Review(submission, PracticeLocationStatus.Approved, "Verified independently", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true).IsSuccess);
        Assert.True(row.ChangeFee(row.UserId, Price(0)).IsSuccess); Assert.Equal(0, row.ConsultationFee); Assert.Equal(50, submission.ConsultationFee);
        Assert.Equal(PracticeLocationErrors.DraftLocked, Assert.Single(row.UpdateDraft(row.UserId, Details("Moved"), Price(0)).Errors));
        Assert.Equal(PracticeLocationErrors.Forbidden, Assert.Single(row.ChangeFee(Guid.NewGuid(), Price(5)).Errors));
    }
    [Fact]
    public void SelfReviewBlankReasonAndNonDecisionStatesNeverChangeApproval()
    {
        var row = Location(); var submission = row.Submit(Guid.NewGuid(), row.UserId, Evidence()).Value;
        Assert.Equal(PracticeLocationErrors.SelfReview, Assert.Single(row.Review(submission, PracticeLocationStatus.Approved, "Own", row.UserId, DateTimeOffset.UnixEpoch, true, true).Errors));
        Assert.Equal(PracticeLocationErrors.ReasonRequired, Assert.Single(row.Review(submission, PracticeLocationStatus.Rejected, " ", Guid.NewGuid(), DateTimeOffset.UnixEpoch, false, false).Errors));
        Assert.Equal(PracticeLocationErrors.InvalidOutcome, Assert.Single(row.Review(submission, PracticeLocationStatus.Draft, "Invalid", Guid.NewGuid(), DateTimeOffset.UnixEpoch, false, false).Errors));
    }
    private static DoctorPracticeLocation Location() => DoctorPracticeLocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Details("Address"), Price(50));
    private static PracticeLocationDetails Details(string address) => PracticeLocationDetails.Create("Name", address, null, null).Value;
    private static ConsultationPrice Price(decimal amount) => ConsultationPrice.Create(amount, "EGP").Value;
    private static PracticeLocationEvidenceMetadata[] Evidence() => [new(PracticeLocationEvidenceKind.FacilityLicense, $"{Guid.NewGuid():N}.pdf", "facility.pdf", "application/pdf", 10, new string('A', 64))];
}
