using ClinIQ.Domain.Clinics;
using Xunit;

namespace ClinIQ.Domain.Tests.Clinics;

public sealed class ClinicRegistrationTests
{
    [Fact]
    public void InitialApplicationDoesNotCreateAnApprovedClinicOrMembership()
    {
        var request = Request(); Assert.Equal(ClinicApplicationStatus.Draft, request.Status);
        Assert.Null(request.ApprovedClinicId); Assert.Null(request.ApprovedBranchId); Assert.Null(request.CurrentSubmissionId);
        var submitted = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        Assert.Equal(ClinicApplicationStatus.PendingReview, request.Status); Assert.Equal(request.ApplicantUserId, submitted.ApplicantUserId);
        Assert.True(Clinic.FromApprovedSubmission(Guid.NewGuid(), submitted).IsError);
        Assert.True(ClinicManagementMembership.FromApprovedSubmission(Guid.NewGuid(), submitted).IsError);
    }
    [Theory]
    [InlineData(ClinicEvidenceKind.FacilityLicense)]
    [InlineData(ClinicEvidenceKind.ApplicantIdentity)]
    public void LicenseAndIdentityAreRequiredIndependently(ClinicEvidenceKind missing)
    {
        var request = Request(); var docs = Documents().Where(x => x.Kind != missing).ToArray();
        Assert.Equal(ClinicErrors.RequiredEvidence, Assert.Single(request.Submit(Guid.NewGuid(), request.ApplicantUserId, docs).Errors));
    }
    [Fact]
    public void SignedDelegationBasisRequiresTheDelegationFile()
    {
        var request = Request(Data() with { ApplicantCapacity = ClinicApplicantCapacity.AuthorizedRepresentative, AuthorityBasis = ManagementAuthorityBasis.SignedDelegation });
        Assert.True(request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).IsError);
        Assert.True(request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents(delegation: true)).IsSuccess);
    }
    [Fact]
    public void RecordedAuthorityEvidenceStillRequiresManualAuthorityAndIndependentChecks()
    {
        var request = Request(); var sub = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        var result = request.Review(sub, ClinicApplicationStatus.Approved, "Insufficient authority", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, false, false, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ClinicErrors.UnverifiedEvidence, Assert.Single(result.Errors)); Assert.Null(request.ApprovedClinicId);
        Assert.Equal(ClinicApplicationStatus.PendingReview, sub.Status);
    }
    [Theory]
    [InlineData(false,true,true,true)]
    [InlineData(true,false,true,true)]
    [InlineData(true,true,false,true)]
    [InlineData(true,true,true,false)]
    public void EveryApprovalCheckIsRequired(bool license, bool identity, bool authority, bool corroborated)
    {
        var request = Request(); var sub = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        Assert.True(request.Review(sub, ClinicApplicationStatus.Approved, "Check", Guid.NewGuid(), DateTimeOffset.UnixEpoch, license, identity, authority, corroborated, Guid.NewGuid(), Guid.NewGuid()).IsError);
    }
    [Theory]
    [InlineData(ClinicApplicationStatus.NeedsInformation)]
    [InlineData(ClinicApplicationStatus.Rejected)]
    public void CorrectionKeepsOldApplicantIdentityAndReviewHistory(ClinicApplicationStatus outcome)
    {
        var request = Request(); var first = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        var documentIds = first.Documents.Select(x => x.Id).ToArray();
        Assert.True(request.Review(first, outcome, "Correct the licensed operator", Guid.NewGuid(), DateTimeOffset.UnixEpoch, false, false, false, false).IsSuccess);
        Assert.True(request.UpdateDraft(request.ApplicantUserId, Data() with { LicensedOperator = "Corrected synthetic operator" }).IsSuccess);
        var second = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        Assert.Equal(2, second.AttemptNumber); Assert.Equal(outcome, first.Status); Assert.Equal("Synthetic operator", first.LicensedOperator);
        Assert.Equal("Corrected synthetic operator", second.LicensedOperator); Assert.Equal(documentIds, first.Documents.Select(x => x.Id));
        Assert.Equal(ClinicErrors.AlreadyReviewed, Assert.Single(request.Review(first, ClinicApplicationStatus.Approved, "Late", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, true, true, Guid.NewGuid(), Guid.NewGuid()).Errors));
    }
    [Fact]
    public void ApprovalProducesClinicBranchAndAuditedNonClinicalMembership()
    {
        var request = Request(); var sub = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        var clinicId = Guid.NewGuid(); var branchId = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        Assert.True(request.Review(sub, ClinicApplicationStatus.Approved, "Verified independently", reviewer, DateTimeOffset.UnixEpoch, true, true, true, true, clinicId, branchId).IsSuccess);
        var clinic = Clinic.FromApprovedSubmission(clinicId, sub).Value; var branch = ClinicBranch.FromApprovedSubmission(branchId, clinicId, sub).Value;
        var membership = ClinicManagementMembership.FromApprovedSubmission(clinicId, sub).Value;
        Assert.Equal(clinic.Id, branch.ClinicId); Assert.Equal(request.ApplicantUserId, membership.UserId); Assert.Equal(reviewer, membership.GrantedByUserId);
        Assert.Equal(DateTimeOffset.UnixEpoch, membership.GrantedAtUtc); Assert.Equal("Africa/Cairo", branch.TimeZoneId);
        Assert.Equal("AUTHORITY", branch.NormalizedLicensingAuthority); Assert.Equal("LICENSE-1", branch.NormalizedLicenseNumber);
        Assert.Equal(ClinicErrors.DraftLocked, Assert.Single(request.UpdateDraft(request.ApplicantUserId, Data()).Errors));
    }
    [Fact]
    public void NewBranchApprovalCannotCreateAnotherClinicOrMembership()
    {
        var target = Guid.NewGuid(); var request = ClinicRegistrationRequest.Create(Guid.NewGuid(), Guid.NewGuid(), target, Data()).Value;
        var sub = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        Assert.Throws<ArgumentException>(() => request.Review(sub, ClinicApplicationStatus.Approved, "Wrong clinic", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, true, true, Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(request.Review(sub, ClinicApplicationStatus.Approved, "Branch approval", Guid.NewGuid(), DateTimeOffset.UnixEpoch, true, true, true, true, target, Guid.NewGuid()).IsSuccess);
        Assert.True(Clinic.FromApprovedSubmission(Guid.NewGuid(), sub).IsError); Assert.True(ClinicManagementMembership.FromApprovedSubmission(target, sub).IsError);
        Assert.True(ClinicBranch.FromApprovedSubmission(Guid.NewGuid(), Guid.NewGuid(), sub).IsError);
    }
    [Fact]
    public void SelfReviewAndOtherApplicantsEditsAreDenied()
    {
        var request = Request(); Assert.Equal(ClinicErrors.Forbidden, Assert.Single(request.UpdateDraft(Guid.NewGuid(), Data()).Errors));
        var sub = request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Value;
        Assert.Equal(ClinicErrors.SelfReview, Assert.Single(request.Review(sub, ClinicApplicationStatus.Approved, "Own", request.ApplicantUserId, DateTimeOffset.UnixEpoch, true, true, true, true, Guid.NewGuid(), Guid.NewGuid()).Errors));
        Assert.Equal(ClinicErrors.DraftLocked, Assert.Single(request.Submit(Guid.NewGuid(), request.ApplicantUserId, Documents()).Errors));
    }
    [Fact]
    public void InvalidCapacityContactAndOversizedNormalizedLicenseKeysFail()
    {
        Assert.True((Data() with { ApplicantCapacity = (ClinicApplicantCapacity)99 }).Normalize().IsError);
        Assert.True((Data() with { PublicPhone = "not a phone" }).Normalize().IsError);
        Assert.True((Data() with { LicenseNumber = new string('\ufb03', 40) }).Normalize().IsError);
        Assert.True((Data() with { Latitude = 30, Longitude = null }).Normalize().IsError);
    }
    private static ClinicRegistrationRequest Request(ClinicApplicationData? data = null) => ClinicRegistrationRequest.Create(Guid.NewGuid(), Guid.NewGuid(), null, data ?? Data()).Value;
    private static ClinicApplicationData Data() => new("Synthetic clinic", "Description", "First branch", "Synthetic address", null, null, "01012345678", " authority ", " license-1 ", "Synthetic operator", ClinicApplicantCapacity.LicensedOperator, ManagementAuthorityBasis.RecordedEvidence);
    private static ClinicEvidenceMetadata[] Documents(bool delegation = false) => (delegation ? Enum.GetValues<ClinicEvidenceKind>() : new[] { ClinicEvidenceKind.FacilityLicense, ClinicEvidenceKind.ApplicantIdentity })
        .Select(x => new ClinicEvidenceMetadata(x, $"{Guid.NewGuid():N}.pdf", "synthetic.pdf", "application/pdf", 10, new string('A',64))).ToArray();
}
