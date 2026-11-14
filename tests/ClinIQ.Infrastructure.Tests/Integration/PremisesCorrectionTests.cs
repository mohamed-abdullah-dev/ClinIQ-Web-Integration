using ClinIQ.Application.Features.Clinics;
using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;
using Microsoft.AspNetCore.Identity;
using ClinIQ.Infrastructure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PremisesCorrectionTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task IndependentApprovalChangesOnlyPhysicalDetailsAndPreservesHistoryFeeAndIdentity()
    {
        var (owner, place) = await Place();
        var row = (await Submit(owner, place.Id, false, Details(place) with { Address = "Corrected suite spelling" }, place.Version)).Value;
        var pendingPlace = await GetPlace(owner, place.Id); Assert.Equal(place, pendingPlace);
        var approved = (await Decide(row)).Value; Assert.Equal("Approved", approved.Status);
        var after = await GetPlace(owner, place.Id);
        Assert.Equal("Corrected suite spelling", after.Address); Assert.Equal(place.Id, after.Id); Assert.Equal(place.CurrentSubmissionId, after.CurrentSubmissionId);
        Assert.Equal(place.DoctorProfileId, after.DoctorProfileId); Assert.Equal(place.ConsultationFee, after.ConsultationFee); Assert.Equal("Approved", after.Status);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(place.Address, (await db.PracticeLocationSubmissions.SingleAsync(x => x.Id == place.CurrentSubmissionId)).Address);
        var stored = await db.PremisesCorrections.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(owner, stored.CreatedByUserId); Assert.Equal(fixture.Admin.Id, stored.LastModifiedByUserId);
        Assert.Equal(place.Address, stored.Original.Address); Assert.Equal(after.Address, stored.Proposed.Address);
        Assert.NotEqual(place.Version, after.Version);
    }
    [SqlServerTheory]
    [InlineData(PremisesCorrectionStatus.Rejected)]
    [InlineData(PremisesCorrectionStatus.NeedsInformation)]
    public async Task NonApprovalKeepsApprovedPlaceAndAllowsNewImmutableAttempt(PremisesCorrectionStatus outcome)
    {
        var (owner, place) = await Place(); var row = (await Submit(owner, place.Id, false, Details(place) with { Name = "Corrected name" }, place.Version)).Value;
        Assert.Equal(outcome.ToString(), (await Decide(row, outcome)).Value.Status); Assert.Equal(place, await GetPlace(owner, place.Id));
        var next = (await Submit(owner, place.Id, false, Details(place) with { Name = "Further correction" }, place.Version)).Value;
        Assert.NotEqual(row.Id, next.Id);
        var history = (await Call(owner, s => s.ListAsync(false, place.Id, null, null, 1, 20, default))).Value;
        Assert.Equal(2, history.TotalCount); Assert.Contains(history.Items, x => x.Id == row.Id && x.Status == outcome.ToString());
    }
    [SqlServerFact]
    public async Task ConcurrentSubmissionsAndOppositeDecisionsEachHaveOneWinner()
    {
        var (owner, place) = await Place(); var data = Details(place) with { Name = "Concurrent correction" };
        var submitted = await Task.WhenAll(Submit(owner, place.Id, false, data, place.Version), Submit(owner, place.Id, false, data, place.Version));
        var row = Assert.Single(submitted, x => x.IsSuccess).Value;
        Assert.Equal(PremisesCorrectionErrors.PendingExists, Assert.Single(Assert.Single(submitted, x => x.IsError).Errors));
        var decisions = await Task.WhenAll(Decide(row), Decide(row, PremisesCorrectionStatus.Rejected));
        Assert.Single(decisions, x => x.IsSuccess); Assert.Single(decisions, x => x.IsError);
        var final = (await Call(owner, s => s.GetAsync(row.Id, false, default))).Value;
        Assert.Equal(final.Status == "Approved" ? data.Name : place.Name, (await GetPlace(owner, place.Id)).Name);
    }
    [SqlServerFact]
    public async Task FeeChangeMakesPendingSnapshotStaleWithoutLosingEitherHistoryOrPrice()
    {
        var (owner, place) = await Place(); var row = (await Submit(owner, place.Id, false, Details(place) with { Name = "Stale correction" }, place.Version)).Value;
        var fee = await fixture.AsAsync(owner, async p => (await p.GetRequiredService<IPracticeLocationService>().ChangeFeeAsync(place.Id, 77, "EGP", place.Version, default)).Value);
        Assert.Equal(PremisesCorrectionErrors.Stale, Assert.Single((await Decide(row)).Errors));
        Assert.Equal("PendingReview", (await Call(owner, s => s.GetAsync(row.Id, false, default))).Value.Status);
        Assert.Equal(77, (await GetPlace(owner, place.Id)).ConsultationFee);
        Assert.True((await Decide(row, PremisesCorrectionStatus.Rejected)).IsSuccess);
        Assert.True((await Submit(owner, place.Id, false, Details(fee) with { Name = "Fresh correction" }, fee.Version)).IsSuccess);
    }
    [SqlServerFact]
    public async Task BranchCorrectionPreservesClinicMembershipAndSourceEvidence()
    {
        var (owner, clinic, branch) = await Branch(); var proposed = PremisesDetails.From(branch) with { Name = "Correct branch spelling", PublicPhone = "01099999999" };
        var row = (await Submit(owner, branch.Id, true, proposed, Convert.ToBase64String(branch.Version))).Value;
        Assert.True((await Decide(row)).IsSuccess);
        await using var db = fixture.Database.CreateContext(); var after = await db.ClinicBranches.SingleAsync(x => x.Id == branch.Id);
        Assert.Equal(proposed.Name, after.Name); Assert.Equal(proposed.PublicPhone, after.PublicPhone); Assert.Equal(branch.ClinicId, after.ClinicId);
        Assert.Equal(branch.SourceSubmissionId, after.SourceSubmissionId); Assert.Equal(1, await db.ClinicManagementMemberships.CountAsync(x => x.ClinicId == clinic));
        Assert.Equal(branch.Name, (await db.ClinicRegistrationSubmissions.SingleAsync(x => x.Id == branch.SourceSubmissionId)).BranchName);
    }
    [SqlServerFact]
    public async Task DuplicateLicenseRollsBackDecisionAndBranchThenAllowsRejection()
    {
        var (owner, _, branch) = await Branch(); var (_, _, other) = await Branch();
        var row = (await Submit(owner, branch.Id, true, PremisesDetails.From(branch) with { LicensingAuthority = other.LicensingAuthority, LicenseNumber = other.LicenseNumber }, Convert.ToBase64String(branch.Version))).Value;
        Assert.Equal(PremisesCorrectionErrors.DuplicateLicense, Assert.Single((await Decide(row)).Errors));
        Assert.Equal("PendingReview", (await Call(owner, s => s.GetAsync(row.Id, false, default))).Value.Status);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(branch.LicenseNumber, (await db.ClinicBranches.SingleAsync(x => x.Id == branch.Id)).LicenseNumber);
        Assert.True((await Decide(row, PremisesCorrectionStatus.Rejected)).IsSuccess);
    }
    [SqlServerFact]
    public async Task ApplicantMustStillHaveMembershipAtSubmissionAndApproval()
    {
        var (owner, clinic, branch) = await Branch(); var row = (await Submit(owner, branch.Id, true, PremisesDetails.From(branch) with { Name = "Correction" }, Convert.ToBase64String(branch.Version))).Value;
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET IsActive=0 WHERE ClinicId={clinic} AND UserId={owner}");
        Assert.Equal(PremisesCorrectionErrors.Forbidden, Assert.Single((await Decide(row)).Errors));
        Assert.Equal(PremisesCorrectionErrors.NotFound, Assert.Single((await Submit(owner, branch.Id, true, row.Proposed, Convert.ToBase64String(branch.Version))).Errors));
        Assert.True((await Call(owner, s => s.GetAsync(row.Id, false, default))).IsSuccess);
        Assert.True((await Decide(row, PremisesCorrectionStatus.Rejected)).IsSuccess);
    }
    [SqlServerFact]
    public async Task ForeignAccountsCannotSubmitReadOrDownloadAndDownloadsAreAudited()
    {
        var (owner, place) = await Place(); var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var row = (await Submit(owner, place.Id, false, Details(place) with { Name = "Private correction" }, place.Version)).Value;
        Assert.Equal(PremisesCorrectionErrors.NotFound, Assert.Single((await Submit(other.UserId, place.Id, false, row.Proposed, place.Version)).Errors));
        Assert.Equal(PremisesCorrectionErrors.NotFound, Assert.Single((await Call(other.UserId, s => s.GetAsync(row.Id, false, default))).Errors));
        var doc = Assert.Single(row.Documents);
        Assert.Equal(PremisesCorrectionErrors.NotFound, Assert.Single((await Call(other.UserId, s => s.DownloadAsync(row.Id, doc.Id, false, default))).Errors));
        foreach (var user in new[] { owner, fixture.Admin.Id })
        {
            var file = (await Call(user, s => s.DownloadAsync(row.Id, doc.Id, user == fixture.Admin.Id, default))).Value;
            await using (file.Content) { using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(DoctorReviewFixture.Evidence, bytes.ToArray()); }
        }
        await using var db = fixture.Database.CreateContext(); Assert.Equal(2, await db.PremisesCorrectionEvidenceAccesses.CountAsync(x => x.DocumentId == doc.Id));
    }
    [SqlServerFact]
    public async Task AdminManagerCannotReviewOwnClinicAndNoSamePremisesCheckMeansNoApproval()
    {
        var (owner, _, branch) = await Branch(); var row = (await Submit(owner, branch.Id, true, PremisesDetails.From(branch) with { Name = "Correction" }, Convert.ToBase64String(branch.Version))).Value;
        await fixture.AsAsync(owner, async p => { var users = p.GetRequiredService<UserManager<AppUser>>(); Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(owner.ToString()))!, "Admin")).Succeeded); return true; });
        Assert.Equal(PremisesCorrectionErrors.SelfReview, Assert.Single((await Call(owner, s => s.ReviewAsync(row.Id, PremisesCorrectionStatus.Approved, "Own", row.Version, true, true, true, default))).Errors));
        Assert.Equal(PremisesCorrectionErrors.Verification, Assert.Single((await Call(fixture.Admin.Id, s => s.ReviewAsync(row.Id, PremisesCorrectionStatus.Approved, "No relocation proof", row.Version, false, true, true, default))).Errors));
    }
    [SqlServerFact]
    public async Task SaveFailureRollsBackApprovalAndPhysicalChangesTogether()
    {
        var (owner, place) = await Place(); var row = (await Submit(owner, place.Id, false, Details(place) with { Name = "Rollback correction" }, place.Version)).Value;
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_CorrectionTest_FailDecision ON dbo.PremisesCorrections AFTER UPDATE AS THROW 51019, 'Synthetic correction persistence failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Decide(row)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_CorrectionTest_FailDecision;"); }
        Assert.Equal(place, await GetPlace(owner, place.Id));
        Assert.Equal("PendingReview", (await Call(owner, s => s.GetAsync(row.Id, false, default))).Value.Status);
        Assert.True((await Decide(row)).IsSuccess);
    }
    [SqlServerFact]
    public async Task SqlRejectsUnscopedRequestsAndDownGuardPreservesHistory()
    {
        var (owner, place) = await Place(); var row = (await Submit(owner, place.Id, false, Details(place) with { Name = "Retained correction" }, place.Version)).Value;
        await using var db = fixture.Database.CreateContext();
        var invalid = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PremisesCorrections SET LocationId=NULL WHERE Id={row.Id}"));
        Assert.Equal(547, invalid.Number);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028134115_PremisesCorrections");
        try
        {
            var down = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028070928_ManagedClinicOnboarding"));
            Assert.Equal(51003, down.Number); Assert.True(await db.PremisesCorrections.AnyAsync(x => x.Id == row.Id));
        }
        finally { await migrator.MigrateAsync(); }
    }
    [SqlServerFact]
    public async Task UnapprovedPlaceInvalidEvidenceAndStaleVersionLeaveNoRequests()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var draft = await fixture.AsAsync(doctor.UserId, async p => (await p.GetRequiredService<IPracticeLocationService>().CreateAsync(new("Place", "Address", null, null, 100, "EGP"), default)).Value);
        Assert.Equal(PremisesCorrectionErrors.NotApproved, Assert.Single((await Submit(doctor.UserId, draft.Id, false, Details(draft) with { Name = "New" }, draft.Version)).Errors));
        var (owner, place) = await Place();
        Assert.Equal(PremisesCorrectionErrors.Stale, Assert.Single((await Submit(owner, place.Id, false, Details(place) with { Name = "New" }, Convert.ToBase64String(new byte[8]))).Errors));
        Assert.Equal(PremisesCorrectionErrors.Evidence, Assert.Single((await Call(owner, s => s.SubmitAsync(place.Id, false, Details(place) with { Name = "New" }, "Reason", place.Version, [], default))).Errors));
        Assert.Equal(PremisesCorrectionErrors.Unauthorized, Assert.Single((await Call(null, s => s.GetAsync(Guid.NewGuid(), false, default))).Errors));
    }
    private Task<T> Call<T>(Guid? actor, Func<IPremisesCorrectionService, Task<T>> action) => fixture.AsAsync(actor, p => action(p.GetRequiredService<IPremisesCorrectionService>()));
    private async Task<Result<PremisesCorrectionDto>> Submit(Guid actor, Guid id, bool branch, PremisesDetails details, string version)
    {
        var docs = PracticeLocationTestData.Uploads();
        try { return await Call(actor, s => s.SubmitAsync(id, branch, details, "Correct a typo at the same premises", version, docs, default)); }
        finally { foreach (var doc in docs) await doc.Content.DisposeAsync(); }
    }
    private Task<Result<PremisesCorrectionDto>> Decide(PremisesCorrectionDto row, PremisesCorrectionStatus outcome = PremisesCorrectionStatus.Approved) =>
        Call(fixture.Admin.Id, s => s.ReviewAsync(row.Id, outcome, "Synthetic premises review", row.Version, true, true, true, default));
    private Task<PracticeLocationDto> GetPlace(Guid owner, Guid id) => fixture.AsAsync(owner, async p => (await p.GetRequiredService<IPracticeLocationService>().GetAsync(id, false, default)).Value.Location);
    private static PremisesDetails Details(PracticeLocationDto p) => new(p.Name, p.Address, p.Latitude, p.Longitude);
    internal async Task<(Guid Owner, PracticeLocationDto Place)> Place()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var draft = await fixture.AsAsync(doctor.UserId, async p => (await p.GetRequiredService<IPracticeLocationService>().CreateAsync(new("Place", "Address", null, null, 100, "EGP"), default)).Value);
        return (doctor.UserId, await ApprovePlace(doctor.UserId, draft));
    }
    private async Task<PracticeLocationDto> ApprovePlace(Guid owner, PracticeLocationDto draft)
    {
        var docs = PracticeLocationTestData.Uploads(); PracticeLocationDto pending;
        try { pending = await fixture.AsAsync(owner, async p => (await p.GetRequiredService<IPracticeLocationService>().SubmitAsync(draft.Id, draft.Version, docs, default)).Value); }
        finally { foreach (var d in docs) await d.Content.DisposeAsync(); }
        return await fixture.AsAsync(fixture.Admin.Id, async p => (await p.GetRequiredService<IPracticeLocationService>().ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value, PracticeLocationStatus.Approved, "Approved", pending.Version, true, true, default)).Value);
    }
    internal async Task<(Guid Owner, Guid ClinicId, ClinicBranch Branch)> Branch()
    {
        var owner = await fixture.UserAsync("ClinicApplicant");
        var draft = await fixture.AsAsync(owner.Id, async p => (await p.GetRequiredService<IClinicService>().CreateAsync(ClinicTestData.Input(), null, default)).Value);
        var docs = ClinicTestData.Uploads(); ClinicApplicationDto pending;
        try { pending = await fixture.AsAsync(owner.Id, async p => (await p.GetRequiredService<IClinicService>().SubmitAsync(draft.Id, draft.Version, docs, default)).Value); }
        finally { foreach (var d in docs) await d.Content.DisposeAsync(); }
        var approved = await fixture.AsAsync(fixture.Admin.Id, async p => (await p.GetRequiredService<IClinicService>().ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value, ClinicApplicationStatus.Approved, "Approved", pending.Version, true, true, true, true, default)).Value);
        await using var db = fixture.Database.CreateContext(); return (owner.Id, approved.ApprovedClinicId!.Value, await db.ClinicBranches.AsNoTracking().SingleAsync(x => x.Id == approved.ApprovedBranchId));
    }
}
