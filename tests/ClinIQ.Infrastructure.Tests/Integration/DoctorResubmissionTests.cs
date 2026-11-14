using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorResubmissions;
using ClinIQ.Application.Features.DoctorResubmissions.Commands;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorResubmissionTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorrectionKeepsHistoryAndRequiresNewApprovalAndEmailConfirmation(bool confirmed)
    {
        var doctor = await fixture.DoctorAsync(confirmed); var oldId = doctor.Receipt.RegistrationId;
        await ResubmissionSupport.Review(fixture, oldId, false);
        var token = await ResubmissionSupport.Link(fixture, doctor.Email);
        var preview = (await Call(s => s.GetAsync(token, default))).Value;
        Assert.Equal("Synthetic review decision", preview.RejectionReason); Assert.Equal(1, preview.AttemptNumber);
        var document = preview.Documents[0];
        var download = (await Call(s => s.DownloadAsync(token, document.Id, default))).Value;
        await using (download.Content) { using var copy = new MemoryStream(); await download.Content.CopyToAsync(copy); Assert.Equal(DoctorReviewFixture.Evidence, copy.ToArray()); }
        var submitted = (await Submit(token)).Value;
        Assert.Equal(oldId, submitted.PreviousRegistrationId); Assert.Equal(2, submitted.AttemptNumber); Assert.Equal("PendingReview", submitted.Status);
        Assert.True((await Call(s => s.GetAsync(token, default))).IsError);
        await using (var db = fixture.Database.CreateContext())
        {
            var attempts = await db.DoctorRegistrations.Include(x => x.Documents).Where(x => x.UserId == doctor.Receipt.UserId).OrderBy(x => x.AttemptNumber).ToArrayAsync();
            Assert.Equal(2, attempts.Length); Assert.False(attempts[0].IsCurrent); Assert.True(attempts[1].IsCurrent);
            Assert.Equal(DoctorRegistrationStatus.Rejected, attempts[0].Status); Assert.Equal("Synthetic Doctor", attempts[0].ProfessionalName);
            Assert.Equal("Corrected Doctor", attempts[1].ProfessionalName); Assert.Equal(doctor.Receipt.UserId, attempts[1].ResubmittedByUserId);
            Assert.Equal(document.Id, attempts[0].Documents.OrderBy(x => x.Kind).First().Id);
            Assert.All(attempts[0].Documents, x => Assert.DoesNotContain(attempts[1].Documents, y => y.StorageKey == x.StorageKey));
            Assert.Null(attempts[1].CreatedByUserId); // A capability has its own explicit audit, never a fabricated login principal.
            Assert.Empty(await db.AuthSessions.Where(x => x.UserId == doctor.Receipt.UserId).ToArrayAsync());
            Assert.Empty(await db.DoctorProfiles.Where(x => x.UserId == doctor.Receipt.UserId).ToArrayAsync());
            Assert.Empty(await db.UserRoles.Where(x => x.UserId == doctor.Receipt.UserId).ToArrayAsync());
            Assert.NotNull((await db.Set<DoctorResubmissionGrant>().SingleAsync(x => x.RegistrationId == oldId)).ConsumedAtUtc);
            Assert.True(await db.DoctorEvidenceAccesses.AnyAsync(x => x.DocumentId == document.Id && x.ViewerUserId == doctor.Receipt.UserId));
        }
        Assert.Equal(SessionErrors.PendingReview, Assert.Single((await Login(doctor.Email)).Errors));
        await ResubmissionSupport.Review(fixture, submitted.RegistrationId, true);
        var login = await Login(doctor.Email);
        if (!confirmed) { Assert.Equal(SessionErrors.EmailUnconfirmed, Assert.Single(login.Errors)); await fixture.ConfirmAsync(doctor.Receipt.UserId); login = await Login(doctor.Email); }
        Assert.True(login.IsSuccess);
        await using var reader = fixture.Database.CreateContext();
        Assert.Equal(submitted.RegistrationId, (await reader.DoctorProfiles.SingleAsync(x => x.UserId == doctor.Receipt.UserId)).DoctorRegistrationId);
        Assert.Equal(2, await reader.DoctorRegistrationReviews.CountAsync(x => x.DoctorRegistrationId == oldId || x.DoctorRegistrationId == submitted.RegistrationId));
        var oldReview = await fixture.AsAsync(fixture.Admin.Id, p => p.GetRequiredService<IDoctorReviewService>().GetAsync(oldId, default));
        Assert.False(oldReview.Value.Registration.IsCurrent); Assert.Equal("Rejected", oldReview.Value.Decision!.Outcome);
    }

    [SqlServerFact]
    public async Task WrongSecretAndOtherAccountsDocumentsDoNotConsumeTheOwnersLink()
    {
        var owner = await Rejected(); var other = await Rejected();
        var token = await ResubmissionSupport.Link(fixture, owner.Email);
        var altered = token[..^1] + (token[^1] == 'A' ? "B" : "A");
        Assert.Equal(DoctorResubmissionErrors.InvalidLink, Assert.Single((await Submit(altered)).Errors));
        Assert.Equal(DoctorResubmissionErrors.InvalidLink, Assert.Single((await Call(s => s.GetAsync("garbage", default))).Errors));
        await using var db = fixture.Database.CreateContext();
        var otherDocument = await db.Set<DoctorEvidenceDocument>().FirstAsync(x => x.DoctorRegistrationId == other.Receipt.RegistrationId);
        Assert.Equal(DoctorReviewErrors.NotFound, Assert.Single((await Call(s => s.DownloadAsync(token, otherDocument.Id, default))).Errors));
        Assert.True((await Submit(token)).IsSuccess);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiryOrChangedSecurityStampInvalidatesProof(bool expiry)
    {
        var doctor = await Rejected(); var token = await ResubmissionSupport.Link(fixture, doctor.Email);
        var oldTime = fixture.Database.Clock.UtcNow;
        try
        {
            if (expiry) fixture.Database.Clock.UtcNow = oldTime.AddHours(1);
            else
                await fixture.AsAsync(null, async p => { var manager = p.GetRequiredService<UserManager<AppUser>>();
                    Assert.True((await manager.UpdateSecurityStampAsync((await manager.FindByIdAsync(doctor.Receipt.UserId.ToString()))!)).Succeeded); return true; });
            Assert.Equal(DoctorResubmissionErrors.InvalidLink, Assert.Single((await Submit(token)).Errors));
            Assert.True((await Call(s => s.GetAsync(token, default))).IsError);
            await using var db = fixture.Database.CreateContext();
            Assert.True((await db.DoctorRegistrations.SingleAsync(x => x.UserId == doctor.Receipt.UserId)).IsCurrent);
            Assert.Null((await db.Set<DoctorResubmissionGrant>().SingleAsync(x => x.RegistrationId == doctor.Receipt.RegistrationId)).ConsumedAtUtc);
        }
        finally { fixture.Database.Clock.UtcNow = oldTime; }
    }

    [SqlServerFact]
    public async Task ConcurrentRedemptionCommitsExactlyOneNewAttempt()
    {
        var doctor = await Rejected(); var token = await ResubmissionSupport.Link(fixture, doctor.Email);
        var results = await Task.WhenAll(Submit(token), Submit(token));
        Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(2, await db.DoctorRegistrations.CountAsync(x => x.UserId == doctor.Receipt.UserId));
        Assert.Equal(1, await db.DoctorRegistrations.CountAsync(x => x.UserId == doctor.Receipt.UserId && x.IsCurrent));
    }

    [SqlServerFact]
    public async Task InvalidCorrectionDoesNotConsumeProofOrRetireRejectedAttempt()
    {
        var doctor = await Rejected(); var token = await ResubmissionSupport.Link(fixture, doctor.Email);
        Assert.True((await Submit(token, docs: false)).IsError);
        Assert.True((await Submit(token, specialty: Guid.NewGuid())).IsError);
        Assert.True((await Call(s => s.GetAsync(token, default))).IsSuccess);
        Assert.True((await Submit(token)).IsSuccess);
        Assert.True((await Submit(token)).IsError);
    }

    [SqlServerFact]
    public async Task FailureAfterWritingEvidenceRollsBackBothSavesAndCleansOnlyNewFiles()
    {
        var doctor = await Rejected(); var token = await ResubmissionSupport.Link(fixture, doctor.Email);
        var directory = Path.Combine(fixture.PrivateRoot, "doctor-evidence"); var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();
        await fixture.AsAsync(null, async p =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(fixture.Connection).AddInterceptors(new FailSuccessor()).Options;
            await using var db = new AppDbContext(options, p.GetRequiredService<IPublisher>());
            var service = new DoctorResubmissionService(db, p.GetRequiredService<UserManager<AppUser>>(), p.GetRequiredService<RegistrationStorage>(),
                fixture.Database.Clock, p.GetRequiredService<IHostEnvironment>(), p.GetRequiredService<IOptions<DoctorResubmissionOptions>>(), NullLogger<DoctorResubmissionService>.Instance);
            var command = ResubmissionSupport.Command(token, fixture.SpecialtyId);
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(command, default)); }
            finally { foreach (var file in command.Documents) await file.Content.DisposeAsync(); }
            return true;
        });
        Assert.Equal(before, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray());
        await using var reader = fixture.Database.CreateContext();
        Assert.True((await reader.DoctorRegistrations.SingleAsync(x => x.UserId == doctor.Receipt.UserId)).IsCurrent);
        Assert.Null((await reader.Set<DoctorResubmissionGrant>().SingleAsync(x => x.RegistrationId == doctor.Receipt.RegistrationId)).ConsumedAtUtc);
        Assert.True((await Submit(token)).IsSuccess);
    }

    [SqlServerFact]
    public async Task LinkRequestIsEligibleOnlyForRejectedInitialRegistrationAndHasPerUserCooldown()
    {
        var pending = await fixture.DoctorAsync(); var approved = await fixture.DoctorAsync();
        await ResubmissionSupport.Review(fixture, approved.Receipt.RegistrationId, true);
        var patient = await fixture.UserAsync("Patient");
        foreach (var email in new[] { "unknown@example.test", pending.Email, approved.Email, patient.Email! })
            await Call(async s => { await s.RequestLinkAsync(email, default); return true; });
        await using (var db = fixture.Database.CreateContext())
            Assert.False(await db.Set<DoctorResubmissionGrant>().AnyAsync(x => x.UserId == pending.Receipt.UserId || x.UserId == approved.Receipt.UserId || x.UserId == patient.Id));
        var rejected = await Rejected(); var token = await ResubmissionSupport.Link(fixture, rejected.Email);
        await Call(async s => { await s.RequestLinkAsync(rejected.Email, default); return true; });
        await using var reader = fixture.Database.CreateContext();
        Assert.Single(await reader.Set<DoctorResubmissionGrant>().Where(x => x.UserId == rejected.Receipt.UserId).ToArrayAsync());
        var time = fixture.Database.Clock.UtcNow;
        try
        {
            fixture.Database.Clock.UtcNow = time.AddMinutes(1);
            var replacement = await ResubmissionSupport.Link(fixture, rejected.Email);
            Assert.NotEqual(token, replacement); Assert.True((await Call(s => s.GetAsync(token, default))).IsError);
            Assert.True((await Call(s => s.GetAsync(replacement, default))).IsSuccess);
        }
        finally { fixture.Database.Clock.UtcNow = time; }
    }

    private async Task<(DoctorRegistrationReceipt Receipt, string Email)> Rejected()
    {
        var doctor = await fixture.DoctorAsync(); await ResubmissionSupport.Review(fixture, doctor.Receipt.RegistrationId, false); return doctor;
    }

    [SqlServerFact]
    public async Task DatabaseGuardsCurrentUniquenessSameAccountLineageAndHistoryRollback()
    {
        var owner = await Rejected(); var other = await Rejected();
        var token = await ResubmissionSupport.Link(fixture, owner.Email); var receipt = (await Submit(token)).Value;
        await using (var db = fixture.Database.CreateContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorRegistrations SET PreviousRegistrationId={other.Receipt.RegistrationId} WHERE Id={receipt.RegistrationId}"));
            Assert.Equal(547, error.Number);
            var migrator = db.GetService<IMigrator>();
            // Test this migration's boundary, independently of later migrations with empty tables.
            await migrator.MigrateAsync("20261027185809_DoctorResubmission");
            try
            {
                var guard = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261027142333_DoctorProfessionalChanges"));
                Assert.Equal(51000, guard.Number);
                Assert.Equal("20261027185809_DoctorResubmission", (await db.Database.GetAppliedMigrationsAsync()).Last());
            }
            finally { await migrator.MigrateAsync(); }
        }
        await using var collision = fixture.Database.CreateContext(); var candidateId = Guid.NewGuid();
        // Attempt 3 and its predecessor are unused: only the current-attempt index should reject this insert.
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => collision.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO DoctorRegistrations (Id,UserId,ProfessionalName,PhoneNumber,RegistrationNumber,SpecialtyId,ProfessionalGrade,
                Status,CreatedAtUtc,IsCurrent,AttemptNumber,PreviousRegistrationId,ResubmittedByUserId)
            SELECT {candidateId},UserId,ProfessionalName,PhoneNumber,RegistrationNumber,SpecialtyId,ProfessionalGrade,
                1,CreatedAtUtc,1,3,Id,UserId FROM DoctorRegistrations WHERE Id={receipt.RegistrationId}
            """));
        Assert.True(duplicate.Number is 2601 or 2627); Assert.Contains("'IX_DoctorRegistrations_UserId'", duplicate.Message);
    }

    [SqlServerFact]
    public async Task ResubmissionDoesNotDisableAnIndependentPatientRole()
    {
        var doctor = await Rejected();
        await fixture.AsAsync(null, async p =>
        {
            var manager = p.GetRequiredService<UserManager<AppUser>>(); var roles = p.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync("Patient")) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Patient") { Id = Guid.NewGuid() })).Succeeded);
            Assert.True((await manager.AddToRoleAsync((await manager.FindByIdAsync(doctor.Receipt.UserId.ToString()))!, "Patient")).Succeeded); return true;
        });
        var token = await ResubmissionSupport.Link(fixture, doctor.Email); Assert.True((await Submit(token)).IsSuccess);
        var mobile = await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().LoginAsync(doctor.Email, DoctorReviewFixture.Password, false, SessionClient.Mobile, default));
        Assert.True(mobile.IsSuccess); Assert.Equal(new[] { "Patient" }, mobile.Value.User.Roles);
        Assert.Equal(SessionErrors.PendingReview, Assert.Single((await Login(doctor.Email)).Errors));
    }
    private Task<T> Call<T>(Func<IDoctorResubmissionService, Task<T>> action) => fixture.AsAsync(null, p => action(p.GetRequiredService<IDoctorResubmissionService>()));
    private async Task<Result<DoctorResubmissionReceipt>> Submit(string token, bool docs = true, Guid? specialty = null)
    {
        var command = ResubmissionSupport.Command(token, specialty ?? fixture.SpecialtyId, docs);
        try { return await Call(s => s.SubmitAsync(command, default)); }
        finally { foreach (var file in command.Documents) await file.Content.DisposeAsync(); }
    }
    private Task<Result<SessionGrant>> Login(string email) => fixture.AsAsync(null,
        p => p.GetRequiredService<ISessionService>().LoginAsync(email, DoctorReviewFixture.Password, false, SessionClient.Web, default));
    private sealed class FailSuccessor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<DoctorRegistration>().Any(x => x.State == EntityState.Added && x.Entity.AttemptNumber > 1))
                throw new InvalidOperationException("Synthetic successor-save failure.");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }
}

internal static class ResubmissionSupport
{
    internal static Task Review(DoctorReviewFixture fixture, Guid id, bool approve) => fixture.AsAsync(fixture.Admin.Id, async p =>
    {
        var service = p.GetRequiredService<IDoctorReviewService>(); var detail = (await service.GetAsync(id, default)).Value;
        Assert.True((await service.ReviewAsync(id, approve, "Synthetic review decision", detail.Version, default)).IsSuccess); return true;
    });
    internal static async Task<string> Link(DoctorReviewFixture fixture, string email)
    {
        await fixture.AsAsync(null, async p => { await p.GetRequiredService<IDoctorResubmissionService>().RequestLinkAsync(email, default); return true; });
        return ReadLink(fixture.PrivateRoot, email);
    }
    internal static string ReadLink(string root, string email)
    {
        foreach (var file in new DirectoryInfo(Path.Combine(root, "doctor-resubmission-emails")).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
            if (json.RootElement.GetProperty("To").GetString() == email)
                return new Uri(json.RootElement.GetProperty("ResubmissionUrl").GetString()!).Fragment[7..];
        }
        throw new InvalidOperationException("Expected a private development resubmission email.");
    }
    internal static ResubmitDoctorCommand Command(string token, Guid specialty, bool docs = true) => new(token, "Corrected Doctor", "01012345678", "CORRECTED",
        specialty, ProfessionalGrade.GeneralPractitioner, docs ? new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
            .Select(x => new RegistrationUpload(x, "synthetic.pdf", "application/pdf", DoctorReviewFixture.Evidence.Length, new MemoryStream(DoctorReviewFixture.Evidence))).ToArray() : []);
}
