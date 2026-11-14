using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using ClinIQ.Infrastructure.PracticeLocations;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLocationTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task DraftInformationRequestCorrectionApprovalAndFeeChangePreserveHistory()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var draft = await Create(doctor.UserId);
        Assert.Equal("Draft", draft.Status); Assert.Null(draft.CurrentSubmissionId);
        var first = (await Submit(doctor.UserId, draft)).Value;
        Assert.Equal("PendingReview", first.Status);
        Assert.Equal(PracticeLocationErrors.UnverifiedEvidence, Assert.Single((await Decide(first, PracticeLocationStatus.Approved, verified: false)).Errors));
        var information = (await Decide(first, PracticeLocationStatus.NeedsInformation)).Value;
        var corrected = (await Call(doctor.UserId, s => s.UpdateDraftAsync(draft.Id, Input() with { Address = "Corrected synthetic address" }, information.Version, default))).Value;
        var second = (await Submit(doctor.UserId, corrected)).Value;
        Assert.Equal(2, second.AttemptNumber); Assert.NotEqual(first.CurrentSubmissionId, second.CurrentSubmissionId);
        var approved = (await Decide(second, PracticeLocationStatus.Approved)).Value;
        var fee = (await Call(doctor.UserId, s => s.ChangeFeeAsync(approved.Id, 0, "EGP", approved.Version, default))).Value;
        Assert.Equal(0, fee.ConsultationFee); Assert.Equal("Approved", fee.Status);
        var history = (await Call(doctor.UserId, s => s.ListSubmissionsAsync(draft.Id, false, 1, 20, default))).Value;
        Assert.Equal(2, history.TotalCount); Assert.Equal("NeedsInformation", history.Items[0].Status);
        Assert.Equal("Synthetic address", history.Items[0].Address); Assert.Equal("Corrected synthetic address", history.Items[1].Address);
        Assert.Equal(100, history.Items[1].ConsultationFee); Assert.Equal("Approved", history.Items[1].Status);
        await using var db = fixture.Database.CreateContext(); var stored = await db.DoctorPracticeLocations.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(doctor.UserId, stored.CreatedByUserId); Assert.Equal(doctor.UserId, stored.LastModifiedByUserId);
        Assert.All(await db.PracticeLocationSubmissions.Where(x => x.LocationId == draft.Id).ToArrayAsync(), x => Assert.Equal(fixture.Admin.Id, x.ReviewerUserId));
    }
    [SqlServerTheory]
    [InlineData("Patient")]
    [InlineData("Admin")]
    [InlineData("Doctor")]
    [InlineData(null)]
    public async Task RolesWithoutApprovedDoctorProfileCannotCreateLocations(string? role)
    {
        var user = await fixture.UserAsync(role);
        Assert.Equal(PracticeLocationErrors.Forbidden, Assert.Single((await Call(user.Id, s => s.CreateAsync(Input(), default))).Errors));
        Assert.Equal(PracticeLocationErrors.Unauthorized, Assert.Single((await Call(null, s => s.CreateAsync(Input(), default))).Errors));
    }
    [SqlServerFact]
    public async Task ApprovalOfAnUnconfirmedDoctorDoesNotPermitManagingLocations()
    {
        var user = await PracticeLocationTestData.ApprovedDoctor(fixture, confirmed: false);
        Assert.Equal(PracticeLocationErrors.Forbidden, Assert.Single((await Call(user.UserId, s => s.CreateAsync(Input(), default))).Errors));
    }
    [SqlServerFact]
    public async Task OwnersAndAdminsCanReadEvidenceButOtherDoctorsCannot()
    {
        var owner = await PracticeLocationTestData.ApprovedDoctor(fixture); var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var pending = (await Submit(owner.UserId, await Create(owner.UserId))).Value;
        var detail = (await Call(owner.UserId, s => s.GetAsync(pending.Id, false, default))).Value;
        var doc = Assert.Single(detail.CurrentSubmission!.Documents);
        Assert.Equal(PracticeLocationErrors.NotFound, Assert.Single((await Call(other.UserId, s => s.GetAsync(pending.Id, false, default))).Errors));
        Assert.Equal(PracticeLocationErrors.NotFound, Assert.Single((await Call(other.UserId, s => s.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, doc.Id, false, default))).Errors));
        foreach (var actor in new[] { owner.UserId, fixture.Admin.Id })
        {
            var file = (await Call(actor, s => s.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, doc.Id, actor == fixture.Admin.Id, default))).Value;
            await using (file.Content) { using var buffer = new MemoryStream(); await file.Content.CopyToAsync(buffer); Assert.Equal(DoctorReviewFixture.Evidence, buffer.ToArray()); }
        }
        await using var db = fixture.Database.CreateContext(); Assert.Equal(2, await db.PracticeLocationEvidenceAccesses.CountAsync(x => x.DocumentId == doc.Id));
        Assert.Equal(PracticeLocationErrors.NotFound, Assert.Single((await Call(owner.UserId, s => s.DownloadAsync(Guid.NewGuid(), pending.CurrentSubmissionId!.Value, doc.Id, false, default))).Errors));
    }
    [SqlServerFact]
    public async Task SelfReviewIsDeniedEvenForDoctorWhoAlsoHoldsAdminRole()
    {
        var owner = await PracticeLocationTestData.ApprovedDoctor(fixture); var pending = (await Submit(owner.UserId, await Create(owner.UserId))).Value;
        await fixture.AsAsync(owner.UserId, async p => { var manager = p.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await manager.AddToRoleAsync((await manager.FindByIdAsync(owner.UserId.ToString()))!, "Admin")).Succeeded); return true; });
        Assert.Equal(PracticeLocationErrors.SelfReview, Assert.Single((await Call(owner.UserId, s => s.ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value,
            PracticeLocationStatus.Approved, "Self", pending.Version, true, true, default))).Errors));
    }
    [SqlServerFact]
    public async Task ConcurrentSubmissionsCommitOneAttemptAndCleanTheLosersFiles()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var draft = await Create(doctor.UserId);
        var results = await Task.WhenAll(Submit(doctor.UserId, draft), Submit(doctor.UserId, draft));
        Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        await using var db = fixture.Database.CreateContext(); var row = await db.PracticeLocationSubmissions.Include(x => x.Documents).SingleAsync(x => x.LocationId == draft.Id);
        Assert.Single(row.Documents); Assert.Equal(1, row.AttemptNumber);
    }
    [SqlServerFact]
    public async Task ConcurrentOppositeDecisionsCommitOneFinalOutcome()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var pending = (await Submit(doctor.UserId, await Create(doctor.UserId))).Value;
        var results = await Task.WhenAll(Decide(pending, PracticeLocationStatus.Approved), Decide(pending, PracticeLocationStatus.Rejected));
        Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        var state = (await Call(doctor.UserId, s => s.GetAsync(pending.Id, false, default))).Value;
        Assert.Equal(state.Location.Status, state.CurrentSubmission!.Status); Assert.NotNull(state.CurrentSubmission.ReviewedAtUtc);
    }
    [SqlServerFact]
    public async Task RejectedLocationCanBeResubmittedWithoutErasingTheRejection()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var first = (await Submit(doctor.UserId, await Create(doctor.UserId))).Value;
        var rejected = (await Decide(first, PracticeLocationStatus.Rejected)).Value;
        var second = (await Submit(doctor.UserId, rejected)).Value;
        Assert.Equal(2, second.AttemptNumber);
        var old = (await Call(doctor.UserId, s => s.GetSubmissionAsync(first.Id, first.CurrentSubmissionId!.Value, false, default))).Value;
        Assert.Equal("Rejected", old.Status); Assert.Equal("Synthetic facility decision", old.ReviewReason);
        Assert.True((await Decide(second, PracticeLocationStatus.Approved)).IsSuccess);
    }
    [SqlServerFact]
    public async Task TwoPlacesAtTheSameAddressStayIndependentAndHaveIndependentPrices()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var first = await Create(doctor.UserId);
        var second = (await Call(doctor.UserId, s => s.CreateAsync(Input() with { ConsultationFee = 200 }, default))).Value;
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(first.Address, second.Address);
        var approved = (await Decide((await Submit(doctor.UserId, first)).Value, PracticeLocationStatus.Approved)).Value;
        var price = await Call(doctor.UserId, s => s.ChangeFeeAsync(first.Id, 150, "EGP", approved.Version, default)); Assert.True(price.IsSuccess);
        Assert.Equal(200, (await Call(doctor.UserId, s => s.GetAsync(second.Id, false, default))).Value.Location.ConsultationFee);
        Assert.Equal(PracticeLocationErrors.Stale, Assert.Single((await Call(doctor.UserId, s => s.ChangeFeeAsync(first.Id, 999, "EGP", approved.Version, default))).Errors));
        Assert.Equal(PracticeLocationErrors.DraftLocked, Assert.Single((await Call(doctor.UserId, s => s.UpdateDraftAsync(first.Id, Input() with { Address = "Moved" }, price.Value.Version, default))).Errors));
    }
    [SqlServerFact]
    public async Task InvalidPriceOrMissingLicenseLeavesDraftAndItsVersionUntouched()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var draft = await Create(doctor.UserId);
        Assert.True((await Call(doctor.UserId, s => s.UpdateDraftAsync(draft.Id, Input() with { Currency = "USD" }, draft.Version, default))).IsError);
        var docs = PracticeLocationTestData.Uploads(PracticeLocationEvidenceKind.WorkAuthorization);
        try { Assert.Equal(PracticeLocationErrors.RequiredEvidence, Assert.Single((await Call(doctor.UserId, s => s.SubmitAsync(draft.Id, draft.Version, docs, default))).Errors)); }
        finally { foreach (var file in docs) await file.Content.DisposeAsync(); }
        var current = (await Call(doctor.UserId, s => s.GetAsync(draft.Id, false, default))).Value;
        Assert.Equal(draft.Version, current.Location.Version); Assert.Null(current.CurrentSubmission);
    }
    [SqlServerFact]
    public async Task TamperedEvidenceIsNotReturnedOrAuditedAsAccessible()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var pending = (await Submit(doctor.UserId, await Create(doctor.UserId))).Value;
        await using var db = fixture.Database.CreateContext();
        var doc = await db.PracticeLocationDocuments.SingleAsync(x => x.SubmissionId == pending.CurrentSubmissionId);
        var path = Path.Combine(fixture.PrivateRoot, "doctor-evidence", doc.StorageKey); var changed = await File.ReadAllBytesAsync(path); changed[^1] ^= 1; await File.WriteAllBytesAsync(path, changed);
        Assert.Equal(PracticeLocationErrors.NotFound, Assert.Single((await Call(doctor.UserId, s => s.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, doc.Id, false, default))).Errors));
        Assert.False(await db.PracticeLocationEvidenceAccesses.AnyAsync(x => x.DocumentId == doc.Id));
    }
    [SqlServerFact]
    public async Task SqlEnforcesProfileOwnershipEgpPriceAndSubmissionScope()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var pending = (await Submit(doctor.UserId, await Create(doctor.UserId))).Value; var foreign = await Create(other.UserId);
        await using var db = fixture.Database.CreateContext();
        var owner = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET UserId={other.UserId} WHERE Id={pending.Id}")); Assert.Equal(547, owner.Number);
        var currency = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET Currency='USD' WHERE Id={pending.Id}")); Assert.Equal(547, currency.Number);
        var fee = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET ConsultationFee=-1 WHERE Id={pending.Id}")); Assert.Equal(547, fee.Number);
        var scope = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET CurrentSubmissionId={pending.CurrentSubmissionId},AttemptNumber=1,Status=2 WHERE Id={foreign.Id}")); Assert.Equal(547, scope.Number);
    }
    [SqlServerFact]
    public async Task FailedSubmissionSaveRollsBackTheCurrentStateAndRemovesItsNewFiles()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var draft = await Create(doctor.UserId);
        var directory = Path.Combine(fixture.PrivateRoot, "doctor-evidence"); var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();
        await fixture.AsAsync(doctor.UserId, async p =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(fixture.Connection).AddInterceptors(new FailSubmission()).Options;
            await using var db = new AppDbContext(options, p.GetRequiredService<IPublisher>());
            var service = new PracticeLocationService(db, p.GetRequiredService<UserManager<AppUser>>(), p.GetRequiredService<ICurrentUser>(), fixture.Database.Clock,
                p.GetRequiredService<RegistrationStorage>(), NullLogger<PracticeLocationService>.Instance);
            var docs = PracticeLocationTestData.Uploads();
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(draft.Id, draft.Version, docs, default)); }
            finally { foreach (var doc in docs) await doc.Content.DisposeAsync(); } return true;
        });
        Assert.Equal(before, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray());
        var state = (await Call(doctor.UserId, s => s.GetAsync(draft.Id, false, default))).Value;
        Assert.Equal("Draft", state.Location.Status); Assert.Equal(draft.Version, state.Location.Version); Assert.Null(state.CurrentSubmission);
    }
    private Task<T> Call<T>(Guid? actor, Func<IPracticeLocationService, Task<T>> action) => fixture.AsAsync(actor, p => action(p.GetRequiredService<IPracticeLocationService>()));

    [SqlServerFact]
    public async Task FailedAccessAuditClosesTheEvidenceStreamAndDoesNotGrantAccess()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var pending = (await Submit(doctor.UserId, await Create(doctor.UserId))).Value;
        await using var reader = fixture.Database.CreateContext(); var document = await reader.PracticeLocationDocuments.FirstAsync(x => x.SubmissionId == pending.CurrentSubmissionId);
        await fixture.AsAsync(doctor.UserId, async p =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(fixture.Connection).AddInterceptors(new FailAccessAudit()).Options;
            await using var db = new AppDbContext(options, p.GetRequiredService<IPublisher>());
            var service = new PracticeLocationService(db, p.GetRequiredService<UserManager<AppUser>>(), p.GetRequiredService<ICurrentUser>(), fixture.Database.Clock,
                p.GetRequiredService<RegistrationStorage>(), NullLogger<PracticeLocationService>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(pending.Id, pending.CurrentSubmissionId!.Value, document.Id, false, default)); return true;
        });
        Assert.False(await reader.PracticeLocationEvidenceAccesses.AnyAsync(x => x.DocumentId == document.Id));
        await using var exclusive = new FileStream(Path.Combine(fixture.PrivateRoot, "doctor-evidence", document.StorageKey), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(document.Length, exclusive.Length);
    }

    [SqlServerFact]
    public async Task DownMigrationCannotRemoveExistingPracticeLocationHistory()
    {
        var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var draft = await Create(doctor.UserId);
        await using var db = fixture.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028043220_IndependentPracticeLocations");
        try
        {
            var rejected = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261027185809_DoctorResubmission"));
            Assert.Equal(51001, rejected.Number); Assert.True(await db.DoctorPracticeLocations.AnyAsync(x => x.Id == draft.Id));
            Assert.Equal("20261028043220_IndependentPracticeLocations", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        finally { await migrator.MigrateAsync(); }
    }

    private sealed class FailAccessAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<PracticeLocationEvidenceAccess>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Synthetic access-audit failure.");
            return base.SavingChangesAsync(data, result, ct);
        }
    }
    private static PracticeLocationDraft Input() => new("Synthetic place", "Synthetic address", null, null, 100, "EGP");
    private async Task<PracticeLocationDto> Create(Guid owner) => (await Call(owner, s => s.CreateAsync(Input(), default))).Value;
    private async Task<Result<PracticeLocationDto>> Submit(Guid owner, PracticeLocationDto row)
    {
        var docs = PracticeLocationTestData.Uploads(); try { return await Call(owner, s => s.SubmitAsync(row.Id, row.Version, docs, default)); }
        finally { foreach (var file in docs) await file.Content.DisposeAsync(); }
    }
    private Task<Result<PracticeLocationDto>> Decide(PracticeLocationDto row, PracticeLocationStatus outcome, bool verified = true) =>
        Call(fixture.Admin.Id, s => s.ReviewAsync(row.Id, row.CurrentSubmissionId!.Value, outcome, "Synthetic facility decision", row.Version, verified, verified, default));
    private sealed class FailSubmission : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<PracticeLocationSubmission>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Synthetic location submission failure.");
            return base.SavingChangesAsync(data, result, ct);
        }
    }
}

internal static class PracticeLocationTestData
{
    internal static async Task<(Guid UserId, string Email)> ApprovedDoctor(DoctorReviewFixture fixture, bool confirmed = true)
    {
        var doctor = await fixture.DoctorAsync(confirmed);
        await fixture.AsAsync(fixture.Admin.Id, async p => { var service = p.GetRequiredService<IDoctorReviewService>();
            var detail = (await service.GetAsync(doctor.Receipt.RegistrationId, default)).Value;
            Assert.True((await service.ReviewAsync(doctor.Receipt.RegistrationId, true, "Synthetic professional approval", detail.Version, default)).IsSuccess); return true; });
        return (doctor.Receipt.UserId, doctor.Email);
    }
    internal static PracticeLocationUpload[] Uploads(PracticeLocationEvidenceKind kind = PracticeLocationEvidenceKind.FacilityLicense) =>
        [new(kind, "facility.pdf", "application/pdf", DoctorReviewFixture.Evidence.Length, new MemoryStream(DoctorReviewFixture.Evidence))];
}
