using ClinIQ.Application.Features.Clinics;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class ClinicOnboardingTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task NonDoctorApplicantGetsClinicBranchAndScopedMembershipOnlyAfterApproval()
    {
        var owner = await fixture.UserAsync(null); var draft = await Create(owner.Id);
        Assert.Equal("Draft", draft.Status); Assert.Null(draft.ApprovedClinicId);
        Assert.Equal(ClinicErrors.Forbidden, Assert.Single((await Call(owner.Id, s => s.ListManagedAsync(1, 20, default))).Errors));
        var pending = (await Submit(owner.Id, draft)).Value;
        await using (var before = fixture.Database.CreateContext())
        { Assert.False(await before.ClinicManagementMemberships.AnyAsync(x => x.UserId == owner.Id)); Assert.False(await before.DoctorProfiles.AnyAsync(x => x.UserId == owner.Id)); }
        var approved = (await Decide(pending, ClinicApplicationStatus.Approved)).Value;
        var clinic = (await Call(owner.Id, s => s.GetManagedAsync(approved.ApprovedClinicId!.Value, default))).Value;
        var branch = (await Call(owner.Id, s => s.GetBranchAsync(clinic.Id, approved.ApprovedBranchId!.Value, default))).Value;
        Assert.Equal(clinic.Id, branch.ClinicId); Assert.Equal("Africa/Cairo", branch.TimeZoneId);
        await using var db = fixture.Database.CreateContext(); var member = await db.ClinicManagementMemberships.SingleAsync(x => x.UserId == owner.Id);
        Assert.Equal(clinic.Id, member.ClinicId); Assert.Equal(fixture.Admin.Id, member.GrantedByUserId); Assert.True(member.IsActive);
        Assert.False(await db.DoctorProfiles.AnyAsync(x => x.UserId == owner.Id));
        var assigned = await (from role in db.UserRoles join name in db.Roles on role.RoleId equals name.Id where role.UserId == owner.Id select name.Name).ToArrayAsync();
        Assert.Equal(new[] { "ClinicManager" }, assigned);
    }
    [SqlServerFact]
    public async Task RequestsForInformationAndRejectionKeepImmutableEvidenceAndCanBeResubmitted()
    {
        var owner = await fixture.UserAsync(null); var draft = await Create(owner.Id); var first = (await Submit(owner.Id, draft)).Value;
        var needs = (await Decide(first, ClinicApplicationStatus.NeedsInformation)).Value;
        var corrected = (await Call(owner.Id, s => s.UpdateDraftAsync(needs.Id, needs.Details with { LicensedOperator = "Corrected synthetic operator" }, needs.Version, default))).Value;
        var second = (await Submit(owner.Id, corrected)).Value; var rejected = (await Decide(second, ClinicApplicationStatus.Rejected)).Value;
        var third = (await Submit(owner.Id, rejected)).Value; Assert.Equal(3, third.AttemptNumber);
        var history = (await Call(owner.Id, s => s.ListSubmissionsAsync(draft.Id, false, 1, 20, default))).Value;
        Assert.Equal(new[] { "NeedsInformation", "Rejected", "PendingReview" }, history.Items.Select(x => x.Status));
        Assert.Equal("Synthetic operator", history.Items[0].Details.LicensedOperator); Assert.Equal("Corrected synthetic operator", history.Items[1].Details.LicensedOperator);
        Assert.NotEqual(history.Items[0].Documents[0].Id, history.Items[1].Documents[0].Id);
        Assert.True((await Decide(third, ClinicApplicationStatus.Approved)).IsSuccess);
    }
    [SqlServerFact]
    public async Task ForeignApplicantsCannotReadApplicationsOrEvidenceAndSelfReviewIsDenied()
    {
        var owner = await fixture.UserAsync(null); var other = await fixture.UserAsync(null); var pending = (await Submit(owner.Id, await Create(owner.Id))).Value;
        var detail = (await Call(owner.Id, s => s.GetAsync(pending.Id, false, default))).Value; var doc = detail.CurrentSubmission!.Documents[0];
        Assert.Equal(ClinicErrors.NotFound, Assert.Single((await Call(other.Id, s => s.GetAsync(pending.Id, false, default))).Errors));
        Assert.Equal(ClinicErrors.NotFound, Assert.Single((await Call(other.Id, s => s.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, doc.Id, false, default))).Errors));
        await fixture.AsAsync(owner.Id, async p => { var manager = p.GetRequiredService<UserManager<AppUser>>(); Assert.True((await manager.AddToRoleAsync((await manager.FindByIdAsync(owner.Id.ToString()))!, "Admin")).Succeeded); return true; });
        Assert.Equal(ClinicErrors.SelfReview, Assert.Single((await Call(owner.Id, s => s.ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value, ClinicApplicationStatus.Approved, "Own", pending.Version, true, true, true, true, default))).Errors));
        foreach (var viewer in new[] { owner.Id, fixture.Admin.Id })
        {
            var file = (await Call(viewer, s => s.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, doc.Id, viewer == fixture.Admin.Id, default))).Value;
            await using (file.Content) { using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(DoctorReviewFixture.Evidence, bytes.ToArray()); }
        }
        await using var db = fixture.Database.CreateContext(); Assert.Equal(2, await db.ClinicEvidenceAccesses.CountAsync(x => x.DocumentId == doc.Id));
    }
    [SqlServerFact]
    public async Task ManagerRoleAloneDoesNotAuthorizeAnotherClinicOrAnewBranch()
    {
        var owner = await fixture.UserAsync(null); var approved = (await Decide((await Submit(owner.Id, await Create(owner.Id))).Value, ClinicApplicationStatus.Approved)).Value;
        var ghost = await fixture.UserAsync("ClinicManager"); var clinicId = approved.ApprovedClinicId!.Value;
        Assert.Equal(ClinicErrors.NotFound, Assert.Single((await Call(ghost.Id, s => s.GetManagedAsync(clinicId, default))).Errors));
        Assert.Equal(ClinicErrors.NotFound, Assert.Single((await Call(ghost.Id, s => s.CreateAsync(ClinicTestData.Input(), clinicId, default))).Errors));
        Assert.Equal(0, (await Call(ghost.Id, s => s.ListManagedAsync(1, 20, default))).Value.TotalCount);
    }
    [SqlServerFact]
    public async Task EachAdditionalBranchNeedsItsOwnReviewAndDoesNotCreateAnotherMembership()
    {
        var owner = await fixture.UserAsync(null); var first = (await Decide((await Submit(owner.Id, await Create(owner.Id))).Value, ClinicApplicationStatus.Approved)).Value;
        var clinicId = first.ApprovedClinicId!.Value;
        var next = (await Call(owner.Id, s => s.CreateAsync(ClinicTestData.Input() with { ClinicName = "Forged parent name", BranchName = "Second branch" }, clinicId, default))).Value;
        Assert.Equal(first.Details.ClinicName, next.Details.ClinicName);
        Assert.Equal(1, (await Call(owner.Id, s => s.ListBranchesAsync(clinicId, 1, 20, default))).Value.TotalCount);
        var approved = (await Decide((await Submit(owner.Id, next)).Value, ClinicApplicationStatus.Approved)).Value;
        Assert.Equal(clinicId, approved.ApprovedClinicId); Assert.NotEqual(first.ApprovedBranchId, approved.ApprovedBranchId);
        Assert.Equal(2, (await Call(owner.Id, s => s.ListBranchesAsync(clinicId, 1, 20, default))).Value.TotalCount);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.ClinicManagementMemberships.CountAsync(x => x.ClinicId == clinicId));
        Assert.Equal(first.Details.ClinicName, (await db.Clinics.SingleAsync(x => x.Id == clinicId)).Name);
    }
    [SqlServerFact]
    public async Task DuplicateLicenseApprovalsAcrossApplicantsPublishOnlyOneClinic()
    {
        var first = await fixture.UserAsync(null); var second = await fixture.UserAsync(null); var data = ClinicTestData.Input();
        var a = (await Submit(first.Id, await Create(first.Id, data))).Value; var b = (await Submit(second.Id, await Create(second.Id, data with { LicensingAuthority = data.LicensingAuthority.ToLowerInvariant(), LicenseNumber = "  " + data.LicenseNumber.ToLowerInvariant() + "  " }))).Value;
        var results = await Task.WhenAll(Decide(a, ClinicApplicationStatus.Approved), Decide(b, ClinicApplicationStatus.Approved));
        Assert.Single(results, x => x.IsSuccess); Assert.Equal(ClinicErrors.ExistingFacility, Assert.Single(Assert.Single(results, x => x.IsError).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.ClinicBranches.CountAsync(x => x.NormalizedLicenseNumber == ClinicApplicationData.LicenseKey(data.LicenseNumber)));
        var loser = results[0].IsError ? first.Id : second.Id; Assert.False(await db.ClinicManagementMemberships.AnyAsync(x => x.UserId == loser));
    }
    [SqlServerFact]
    public async Task ConcurrentOppositeDecisionsPublishConsistentStateOnce()
    {
        var owner = await fixture.UserAsync(null); var pending = (await Submit(owner.Id, await Create(owner.Id))).Value;
        var results = await Task.WhenAll(Decide(pending, ClinicApplicationStatus.Approved), Decide(pending, ClinicApplicationStatus.Rejected));
        Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        var state = (await Call(owner.Id, s => s.GetAsync(pending.Id, false, default))).Value;
        Assert.Equal(state.Application.Status, state.CurrentSubmission!.Status);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(state.Application.Status == "Approved", await db.ClinicManagementMemberships.AnyAsync(x => x.UserId == owner.Id));
    }
    [SqlServerFact]
    public async Task ConcurrentSubmissionsCreateOnlyOneCurrentSnapshot()
    {
        var owner = await fixture.UserAsync(null); var draft = await Create(owner.Id);
        var results = await Task.WhenAll(Submit(owner.Id, draft), Submit(owner.Id, draft)); Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.ClinicRegistrationSubmissions.CountAsync(x => x.RequestId == draft.Id));
    }
    [SqlServerFact]
    public async Task UnconfirmedAccountsCannotDraftOrReview()
    {
        var applicant = await fixture.UserAsync(null, confirmed: false);
        Assert.Equal(ClinicErrors.Forbidden, Assert.Single((await Call(applicant.Id, s => s.CreateAsync(ClinicTestData.Input(), null, default))).Errors));
        Assert.Equal(ClinicErrors.Unauthorized, Assert.Single((await Call(null, s => s.ListAsync(false, 1, 20, null, default))).Errors));
        var admin = await fixture.UserAsync("Admin", confirmed: false);
        Assert.Equal(ClinicErrors.Forbidden, Assert.Single((await Call(admin.Id, s => s.ListAsync(true, 1, 20, null, default))).Errors));
    }
    [SqlServerFact]
    public async Task DisabledMembershipImmediatelyStopsManagementButKeepsOwnReviewHistory()
    {
        var owner = await fixture.UserAsync(null); var approved = (await Decide((await Submit(owner.Id, await Create(owner.Id))).Value, ClinicApplicationStatus.Approved)).Value;
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET IsActive=0 WHERE UserId={owner.Id}");
        Assert.Equal(ClinicErrors.NotFound, Assert.Single((await Call(owner.Id, s => s.GetManagedAsync(approved.ApprovedClinicId!.Value, default))).Errors));
        Assert.True((await Call(owner.Id, s => s.GetAsync(approved.Id, false, default))).IsSuccess);
    }
    [SqlServerFact]
    public async Task SqlPreventsMembershipFromBeingAttachedToAnotherAccountOrClinic()
    {
        var owner = await fixture.UserAsync(null); var other = await fixture.UserAsync(null);
        var approved = (await Decide((await Submit(owner.Id, await Create(owner.Id))).Value, ClinicApplicationStatus.Approved)).Value;
        await using var db = fixture.Database.CreateContext();
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET UserId={other.Id} WHERE ClinicId={approved.ApprovedClinicId}")); Assert.Equal(547, error.Number);
    }
    private Task<T> Call<T>(Guid? actor, Func<IClinicService, Task<T>> action) => fixture.AsAsync(actor, p => action(p.GetRequiredService<IClinicService>()));
    [SqlServerFact]
    public async Task MembershipPersistenceFailureRollsBackApprovalClinicBranchAndRoleGrant()
    {
        var owner=await fixture.UserAsync(null);var pending=(await Submit(owner.Id,await Create(owner.Id))).Value;
        await using var setup=fixture.Database.CreateContext();
        Assert.False(await setup.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.triggers WHERE name='TR_ClinicTest_RejectMembership'").SingleAsync()>0);
        await setup.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_ClinicTest_RejectMembership ON dbo.ClinicManagementMemberships AFTER INSERT AS THROW 51009, 'Synthetic membership persistence failure.', 1;");
        try{await Assert.ThrowsAsync<DbUpdateException>(()=>Decide(pending,ClinicApplicationStatus.Approved));}
        finally{await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_ClinicTest_RejectMembership;");}
        var state=(await Call(owner.Id,s=>s.GetAsync(pending.Id,false,default))).Value.Application;
        Assert.Equal("PendingReview",state.Status);Assert.Equal(pending.Version,state.Version);Assert.Null(state.ApprovedClinicId);
        Assert.False(await setup.Clinics.AnyAsync(x=>x.SourceSubmissionId==pending.CurrentSubmissionId));
        Assert.False(await setup.ClinicBranches.AnyAsync(x=>x.SourceSubmissionId==pending.CurrentSubmissionId));
        Assert.False(await setup.ClinicManagementMemberships.AnyAsync(x=>x.UserId==owner.Id));
        Assert.False(await(from r in setup.UserRoles join n in setup.Roles on r.RoleId equals n.Id where r.UserId==owner.Id&&n.NormalizedName=="CLINICMANAGER" select r).AnyAsync());
        Assert.True((await Decide(pending,ClinicApplicationStatus.Approved)).IsSuccess);
    }
    private async Task<ClinicApplicationDto> Create(Guid owner, ClinicApplicationData? data = null) => (await Call(owner, s => s.CreateAsync(data ?? ClinicTestData.Input(), null, default))).Value;
    private async Task<Result<ClinicApplicationDto>> Submit(Guid owner, ClinicApplicationDto row)
    {
        var docs = ClinicTestData.Uploads(row.Details.AuthorityBasis == ManagementAuthorityBasis.SignedDelegation);
        try { return await Call(owner, s => s.SubmitAsync(row.Id, row.Version, docs, default)); } finally { foreach (var doc in docs) await doc.Content.DisposeAsync(); }
    }
    private Task<Result<ClinicApplicationDto>> Decide(ClinicApplicationDto row, ClinicApplicationStatus outcome) => Call(fixture.Admin.Id,
        s => s.ReviewAsync(row.Id, row.CurrentSubmissionId!.Value, outcome, "Synthetic management review", row.Version, true, true, true, true, default));
}

internal static class ClinicTestData
{
    internal static ClinicApplicationData Input() => new("Synthetic clinic", "Synthetic description", "First branch", "Synthetic address", null, null, "01012345678",
        "Synthetic licensing authority", "LICENSE-" + Guid.NewGuid().ToString("N"), "Synthetic operator", ClinicApplicantCapacity.LicensedOperator, ManagementAuthorityBasis.RecordedEvidence);
    internal static ClinicEvidenceUpload[] Uploads(bool delegation = false) => (delegation ? Enum.GetValues<ClinicEvidenceKind>() : new[] { ClinicEvidenceKind.FacilityLicense, ClinicEvidenceKind.ApplicantIdentity })
        .Select(x => new ClinicEvidenceUpload(x, "synthetic.pdf", "application/pdf", DoctorReviewFixture.Evidence.Length, new MemoryStream(DoctorReviewFixture.Evidence))).ToArray();
}
