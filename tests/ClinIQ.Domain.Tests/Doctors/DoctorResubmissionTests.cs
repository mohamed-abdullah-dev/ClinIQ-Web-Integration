using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Doctors;

public sealed class DoctorResubmissionTests
{
    [Fact]
    public void ResubmissionPreservesRejectedSnapshotAndStartsANewPendingAttempt()
    {
        var old = Create(); var ids = old.Documents.Select(x => x.Id).ToArray();
        var decision = old.Review(false, "Replace unreadable documents", Guid.NewGuid(), DateTimeOffset.UtcNow).Value;
        var next = Resubmit(old).Value;
        Assert.False(old.IsCurrent); Assert.Equal(DoctorRegistrationStatus.Rejected, old.Status);
        Assert.Equal("Original", old.ProfessionalName); Assert.Equal(ids, old.Documents.Select(x => x.Id));
        Assert.Equal("Replace unreadable documents", decision.Reason);
        Assert.True(next.IsCurrent); Assert.Equal(2, next.AttemptNumber); Assert.Equal(old.Id, next.PreviousRegistrationId);
        Assert.Equal(old.UserId, next.UserId); Assert.Equal(old.UserId, next.ResubmittedByUserId);
        Assert.Equal(DoctorRegistrationStatus.PendingReview, next.Status); Assert.Equal("Corrected", next.ProfessionalName);
        Assert.DoesNotContain(next.Documents, x => ids.Contains(x.Id));
        Assert.Equal(DoctorResubmissionErrors.NotRejected, Assert.Single(Resubmit(old).Errors));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingAndApprovedAttemptsCannotBeResubmitted(bool approved)
    {
        var row = Create(); if (approved) row.Review(true, "Valid", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(DoctorResubmissionErrors.NotRejected, Assert.Single(Resubmit(row).Errors)); Assert.True(row.IsCurrent);
    }
    [Fact]
    public void InvalidCorrectionDoesNotRetireCurrentAttempt()
    {
        var row = Create(); row.Review(false, "Incomplete", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.True(row.Resubmit(Guid.NewGuid(), "", "01012345678", "X", row.SpecialtyId, row.ProfessionalGrade, Evidence()).IsError);
        Assert.True(row.IsCurrent);
    }
    [Fact]
    public void RepeatedRejectionsKeepAChainAndOnlyCurrentAttemptCanBeReviewed()
    {
        var first = Create(); first.Review(false, "First", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var second = Resubmit(first).Value; second.Review(false, "Second", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var third = Resubmit(second).Value;
        Assert.Equal(3, third.AttemptNumber); Assert.Equal(second.Id, third.PreviousRegistrationId);
        Assert.Equal(DoctorReviewErrors.AlreadyReviewed, Assert.Single(first.Review(true, "Late", Guid.NewGuid(), DateTimeOffset.UtcNow).Errors));
    }
    private static DoctorRegistration Create() => DoctorRegistration.Create(Guid.NewGuid(), Guid.NewGuid(), "Original", "01012345678", "X",
        Guid.NewGuid(), ProfessionalGrade.GeneralPractitioner, Evidence()).Value;
    private static ClinIQ.Domain.Common.Results.Result<DoctorRegistration> Resubmit(DoctorRegistration old) =>
        old.Resubmit(Guid.NewGuid(), "Corrected", "01012345678", "Y", old.SpecialtyId, old.ProfessionalGrade, Evidence());
    private static DoctorEvidenceMetadata[] Evidence() => new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
        .Select(x => new DoctorEvidenceMetadata(x, $"{Guid.NewGuid():N}.pdf", "synthetic.pdf", "application/pdf", 10, new string('A', 64))).ToArray();
}
