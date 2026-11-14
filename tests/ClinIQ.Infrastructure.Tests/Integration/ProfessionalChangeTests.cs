using System.Text;
using System.Text.Json;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorProfessionalChanges;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Application.Features.Specialties;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Doctors;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class ProfessionalChangeTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private Task<T> As<T>(Guid? actor, Func<IProfessionalChangeService, Task<T>> action) =>
        fixture.AsAsync(actor, provider => action(provider.GetRequiredService<IProfessionalChangeService>()));
    private async Task<Guid> Doctor()
    {
        var doctor = await fixture.DoctorAsync();
        await fixture.AsAsync(fixture.Admin.Id, async provider =>
        {
            var reviews = provider.GetRequiredService<IDoctorReviewService>();
            var detail = (await reviews.GetAsync(doctor.Receipt.RegistrationId, default)).Value;
            Assert.True((await reviews.ReviewAsync(detail.Registration.Id, true, "Synthetic approval", detail.Version, default)).IsSuccess);
            return true;
        });
        return doctor.Receipt.UserId;
    }
    private Task<Result<VerifiedDoctorProfileDto>> Profile(Guid doctor) => As(doctor, service => service.GetOwnProfileAsync(default));
    private async Task<Result<ProfessionalChangeSummary>> Submit(Guid doctor, string? version = null, Guid? specialty = null, ProfessionalGrade? grade = null)
    {
        var profile = (await Profile(doctor)).Value;
        var uploads = Enum.GetValues<DoctorEvidenceKind>().Select(kind =>
        {
            var bytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSynthetic change evidence only.\n%%EOF");
            return new RegistrationUpload(kind, "synthetic.pdf", "application/pdf", bytes.Length, new MemoryStream(bytes));
        }).ToArray();
        try { return await As(doctor, service => service.SubmitAsync("Changed " + Guid.NewGuid().ToString("N"), specialty ?? profile.SpecialtyId,
            grade ?? profile.ProfessionalGrade, version ?? profile.Version, uploads, default)); }
        finally { foreach (var file in uploads) await file.Content.DisposeAsync(); }
    }
    private Task<Result<ProfessionalChangeSummary>> Review(Guid id, string version, bool approve = true, Guid? actor = null) =>
        As(actor ?? fixture.Admin.Id, service => service.ReviewAsync(id, approve, "Evidence reviewed", version, default));

    [SqlServerFact]
    public async Task PendingChangesDoNotPublishOrDisableAnApprovedDoctorsLogin()
    {
        var doctor = await Doctor(); var before = (await Profile(doctor)).Value;
        var request = (await Submit(doctor)).Value;
        Assert.Equal("PendingReview", request.Status); Assert.Equal(before, (await Profile(doctor)).Value);
        await using var db = fixture.Database.CreateContext();
        var user = await db.Users.SingleAsync(x => x.Id == doctor);
        await using var scope = fixture.Services.CreateAsyncScope();
        var login = await scope.ServiceProvider.GetRequiredService<ISessionService>().LoginAsync(user.Email!, DoctorReviewFixture.Password, false, SessionClient.Web, default);
        Assert.True(login.IsSuccess);
        Assert.Equal(DoctorRegistrationStatus.Approved, (await db.DoctorRegistrations.SingleAsync(x => x.UserId == doctor)).Status);
        Assert.Equal(doctor, (await db.DoctorProfessionalChanges.SingleAsync(x => x.Id == request.Id)).CreatedByUserId);
    }

    [SqlServerFact]
    public async Task ApprovalPublishesOnlyTheRequestedFieldsWithTrustedDecisionAndUnchangedAccountAndInitialSubmission()
    {
        var doctor = await Doctor(); var before = (await Profile(doctor)).Value;
        await using var db = fixture.Database.CreateContext();
        var account = await db.Users.AsNoTracking().SingleAsync(x => x.Id == doctor);
        var initial = await db.DoctorRegistrations.AsNoTracking().SingleAsync(x => x.UserId == doctor);
        var request = (await Submit(doctor, grade: ProfessionalGrade.Consultant)).Value;
        var approved = await Review(request.Id, request.Version); Assert.True(approved.IsSuccess);
        var after = (await Profile(doctor)).Value;
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.UserId, after.UserId);
        Assert.Equal(request.ProfessionalName, after.ProfessionalName); Assert.Equal(ProfessionalGrade.Consultant, after.ProfessionalGrade);
        Assert.NotEqual(before.Version, after.Version);
        var stored = await db.DoctorProfessionalChanges.AsNoTracking().SingleAsync(x => x.Id == request.Id);
        Assert.Equal(fixture.Admin.Id, stored.ReviewerUserId); Assert.Equal(fixture.Database.Clock.GetUtcNow(), stored.ReviewedAtUtc);
        Assert.Equal(before.ProfessionalName, stored.OriginalProfessionalName); Assert.Equal(before.Version, Convert.ToBase64String(stored.OriginalProfileVersion));
        Assert.Equal(fixture.Admin.Id, stored.LastModifiedByUserId);
        var sameAccount = await db.Users.AsNoTracking().SingleAsync(x => x.Id == doctor);
        Assert.Equal(account.SecurityStamp, sameAccount.SecurityStamp); Assert.Equal(account.FullName, sameAccount.FullName);
        Assert.Equal(initial.ProfessionalName, (await db.DoctorRegistrations.AsNoTracking().SingleAsync(x => x.Id == initial.Id)).ProfessionalName);
        Assert.Equal(0, await db.AuthSessions.CountAsync(x => x.UserId == doctor));
    }

    [SqlServerFact]
    public async Task RejectionLeavesTheActiveProfileUntouchedAndAllowsAnotherRequest()
    {
        var doctor = await Doctor(); var profile = (await Profile(doctor)).Value; var first = (await Submit(doctor)).Value;
        Assert.True((await Review(first.Id, first.Version, false)).IsSuccess);
        Assert.Equal(profile, (await Profile(doctor)).Value);
        Assert.True((await Submit(doctor)).IsSuccess);
        Assert.Equal("ProfessionalChange.AlreadyReviewed", (await Review(first.Id, first.Version)).Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task ConcurrentSubmissionsHaveOnePendingWinner()
    {
        var doctor = await Doctor(); var profile = (await Profile(doctor)).Value;
        var results = await Task.WhenAll(Submit(doctor, profile.Version), Submit(doctor, profile.Version));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("ProfessionalChange.PendingExists", results.Single(result => result.IsError).Errors.Single().Code);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.DoctorProfessionalChanges.CountAsync(x => x.UserId == doctor));
    }

    [SqlServerFact]
    public async Task ConcurrentApproveRejectProduceOneFinalDecision()
    {
        var doctor = await Doctor(); var before = (await Profile(doctor)).Value; var request = (await Submit(doctor)).Value;
        var results = await Task.WhenAll(Review(request.Id, request.Version), Review(request.Id, request.Version, false));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("ProfessionalChange.AlreadyReviewed", results.Single(result => result.IsError).Errors.Single().Code);
        var result = results.Single(x => x.IsSuccess).Value;
        Assert.Equal(result.Status == "Approved" ? request.ProfessionalName : before.ProfessionalName, (await Profile(doctor)).Value.ProfessionalName);
    }

    [SqlServerFact]
    public async Task OldProfileAndRequestVersionsCannotPublish()
    {
        var doctor = await Doctor();
        Assert.Equal("ProfessionalChange.StaleProfile", (await Submit(doctor, "AAAAAAAAAAA=")).Errors.Single().Code);
        var request = (await Submit(doctor)).Value;
        Assert.Equal("ProfessionalChange.StaleRequest", (await Review(request.Id, "AAAAAAAAAAA=")).Errors.Single().Code);
        await using var db = fixture.Database.CreateContext();
        var profile = await db.DoctorProfiles.SingleAsync(x => x.UserId == doctor);
        db.Entry(profile).Property(x => x.ProfessionalName).CurrentValue = "External new baseline"; await db.SaveChangesAsync();
        Assert.Equal("ProfessionalChange.StaleProfile", (await Review(request.Id, request.Version)).Errors.Single().Code);
        Assert.True((await Review(request.Id, request.Version, false)).IsSuccess);
    }

    [SqlServerFact]
    public async Task OwnAndAdminReadsAndDownloadsNeverExposeOtherDoctorsEvidence()
    {
        var doctor = await Doctor(); var other = await Doctor(); var request = (await Submit(doctor)).Value;
        var detail = (await As(doctor, service => service.GetAsync(request.Id, false, default))).Value;
        Assert.Equal(4, detail.Documents.Length);
        Assert.Equal("ProfessionalChange.NotFound", (await As(other, service => service.GetAsync(request.Id, false, default))).Errors.Single().Code);
        var document = detail.Documents[0];
        Assert.Equal("ProfessionalChange.NotFound", (await As(other, service => service.DownloadAsync(request.Id, document.Id, false, default))).Errors.Single().Code);
        Assert.Equal("ProfessionalChange.NotFound", (await As(doctor, service => service.DownloadAsync(Guid.NewGuid(), document.Id, false, default))).Errors.Single().Code);
        var own = (await As(doctor, service => service.DownloadAsync(request.Id, document.Id, false, default))).Value;
        await own.Content.DisposeAsync();
        var admin = (await As(fixture.Admin.Id, service => service.DownloadAsync(request.Id, document.Id, true, default))).Value;
        await admin.Content.DisposeAsync();
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(2, await db.ProfessionalChangeEvidenceAccesses.CountAsync(x => x.DocumentId == document.Id));
        var json = JsonSerializer.Serialize(detail); Assert.DoesNotContain("StorageKey", json); Assert.DoesNotContain("Sha256", json);
        Assert.All((await As(other, service => service.ListAsync(false, 1, 100, null, default))).Value.Items, row => Assert.Equal(other, row.UserId));
    }

    [SqlServerFact]
    public async Task TamperedPrivateEvidenceIsRejectedBeforeAccessIsGranted()
    {
        var doctor = await Doctor(); var request = (await Submit(doctor)).Value;
        await using var db = fixture.Database.CreateContext(); var document = await db.ProfessionalChangeDocuments.FirstAsync(x => x.RequestId == request.Id);
        await File.WriteAllBytesAsync(Path.Combine(fixture.PrivateRoot, "doctor-evidence", document.StorageKey), [1, 2, 3]);
        Assert.Equal("ProfessionalChange.EvidenceUnavailable", (await As(doctor, service => service.DownloadAsync(request.Id, document.Id, false, default))).Errors.Single().Code);
        Assert.Equal(0, await db.ProfessionalChangeEvidenceAccesses.CountAsync(x => x.DocumentId == document.Id));
    }

    [SqlServerTheory]
    [InlineData("Patient")]
    [InlineData("Admin")]
    [InlineData("Doctor")]
    public async Task RoleAloneCannotReplaceAnApprovedProfile(string role)
    {
        var user = await fixture.UserAsync(role);
        Assert.Equal("ProfessionalChange.Forbidden", (await Profile(user.Id)).Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task AnAdminCannotReviewTheirOwnApprovedDoctorChange()
    {
        var doctor = await Doctor();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(doctor.ToString()))!, "Admin")).Succeeded);
        }
        var request = (await Submit(doctor)).Value;
        Assert.Equal("ProfessionalChange.SelfReview", (await Review(request.Id, request.Version, actor: doctor)).Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task AuditFailureDuringDownloadDisposesTheStreamAndReturnsNoDownload()
    {
        var doctor = await Doctor(); var request = (await Submit(doctor)).Value;
        await using var db = fixture.Database.CreateContext(); var document = await db.ProfessionalChangeDocuments.FirstAsync(x => x.RequestId == request.Id);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_SyntheticChangeAccessFailure ON ProfessionalChangeEvidenceAccesses INSTEAD OF INSERT AS BEGIN THROW 51000,'Synthetic access failure.',1; END;");
        try { await Assert.ThrowsAsync<DbUpdateException>(async () => await As(doctor, service => service.DownloadAsync(request.Id, document.Id, false, default))); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_SyntheticChangeAccessFailure"); }
        using var exclusive = new FileStream(Path.Combine(fixture.PrivateRoot, "doctor-evidence", document.StorageKey), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, await db.ProfessionalChangeEvidenceAccesses.CountAsync(x => x.DocumentId == document.Id));
    }

    [SqlServerFact]
    public async Task ProfilePublicationFailureRollsBackTheDecision()
    {
        var doctor = await Doctor(); var before = (await Profile(doctor)).Value; var request = (await Submit(doctor)).Value;
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_SyntheticProfilePublishFailure ON DoctorProfiles INSTEAD OF UPDATE AS BEGIN THROW 51000,'Synthetic publish failure.',1; END;");
        try { await Assert.ThrowsAsync<DbUpdateException>(async () => await Review(request.Id, request.Version)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_SyntheticProfilePublishFailure"); }
        Assert.Equal(before, (await Profile(doctor)).Value);
        var stored = await db.DoctorProfessionalChanges.AsNoTracking().SingleAsync(x => x.Id == request.Id);
        Assert.Equal(ProfessionalChangeStatus.PendingReview, stored.Status); Assert.Null(stored.ReviewerUserId);
        Assert.Equal(request.Version, Convert.ToBase64String(stored.Version));
    }

    [SqlServerFact]
    public async Task FailedSubmissionRemovesOnlyItsOwnFilesAndRollsBackTheRequest()
    {
        var doctor = await Doctor(); var directory = Path.Combine(fixture.PrivateRoot, "doctor-evidence");
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_SyntheticChangeInsertFailure ON DoctorProfessionalChanges INSTEAD OF INSERT AS BEGIN THROW 51000,'Synthetic request failure.',1; END;");
        try { await Assert.ThrowsAsync<DbUpdateException>(async () => await Submit(doctor)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_SyntheticChangeInsertFailure"); }
        Assert.Equal(before, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(0, await db.DoctorProfessionalChanges.CountAsync(x => x.UserId == doctor));
    }

    [SqlServerFact]
    public async Task PendingProposedSpecialtyCannotBeDeletedFromTheAdminCatalog()
    {
        var doctor = await Doctor(); await using var db = fixture.Database.CreateContext();
        var specialty = Specialty.Create("مقترح " + Guid.NewGuid().ToString("N"), "Proposed " + Guid.NewGuid().ToString("N")).Value;
        db.Specialties.Add(specialty); await db.SaveChangesAsync();
        var request = (await Submit(doctor, specialty: specialty.Id)).Value;
        await fixture.AsAsync(fixture.Admin.Id, async provider =>
        {
            var catalog = provider.GetRequiredService<ISpecialtyAdministrationService>(); var row = (await catalog.GetAsync(specialty.Id, default)).Value;
            Assert.Equal("Specialty.InUse", (await catalog.DeleteAsync(row.Id, row.Version, default)).Errors.Single().Code); return true;
        });
        Assert.Equal("PendingReview", request.Status);
    }

    [SqlServerFact]
    public async Task CompositeProfileForeignKeyRejectsAMismatchedRequestOwner()
    {
        var doctor = await Doctor(); var other = await fixture.UserAsync("Patient");
        await using var db = fixture.Database.CreateContext();
        var profile = await db.DoctorProfiles.SingleAsync(x => x.UserId == doctor);
        var metadata = new DoctorEvidenceMetadata(DoctorEvidenceKind.Identity, Guid.NewGuid().ToString("N"), "synthetic.pdf", "application/pdf", 20, new string('A', 64));
        var request = DoctorProfessionalChange.Create(Guid.NewGuid(), profile, "Changed owner check", profile.SpecialtyId, profile.ProfessionalGrade, [metadata]).Value;
        db.DoctorProfessionalChanges.Add(request); db.Entry(request).Property(x => x.UserId).CurrentValue = other.Id;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(547, Assert.IsType<SqlException>(error.InnerException).Number);
    }

    [SqlServerFact]
    public async Task MigrationPreservesAnExistingApprovedProfileAndAddsItsVersion()
    {
        var database = new SqlServerIntegrationFixture();
        try
        {
            await database.InitializeAsync();
            await using var db = database.CreateContext(); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261027133457_SpecialtyAdministration");
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @user uniqueidentifier=NEWID(),@specialty uniqueidentifier=NEWID(),@registration uniqueidentifier=NEWID(),@profile uniqueidentifier=NEWID();
                INSERT INTO AspNetUsers(Id,FullName,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
                VALUES(@user,N'Synthetic upgrade doctor',1,0,0,0,0);
                INSERT INTO Specialties(Id,NameAr,NameEn,NormalizedNameAr,NormalizedNameEn,CreatedAtUtc)
                VALUES(@specialty,N'تخصص ترقية',N'Upgrade',N'تخصص ترقية',N'UPGRADE','2026-10-25T12:00:00+00:00');
                INSERT INTO DoctorRegistrations(Id,UserId,ProfessionalName,PhoneNumber,RegistrationNumber,SpecialtyId,ProfessionalGrade,Status,CreatedAtUtc)
                VALUES(@registration,@user,N'Approved upgrade doctor',N'01012345678',N'SYNTHETIC',@specialty,1,2,'2026-10-25T12:00:00+00:00');
                INSERT INTO DoctorProfiles(Id,UserId,DoctorRegistrationId,ProfessionalName,SpecialtyId,ProfessionalGrade,CreatedAtUtc)
                VALUES(@profile,@user,@registration,N'Approved upgrade doctor',@specialty,1,'2026-10-25T12:00:00+00:00');
                """);
            await migrator.MigrateAsync();
            var profile = await db.DoctorProfiles.AsNoTracking().SingleAsync();
            Assert.Equal("Approved upgrade doctor", profile.ProfessionalName); Assert.Equal(ProfessionalGrade.GeneralPractitioner, profile.ProfessionalGrade);
            Assert.Equal(8, profile.Version.Length); Assert.Null(profile.CreatedByUserId);
            Assert.Empty(await db.DoctorProfessionalChanges.ToArrayAsync());
        }
        finally { await database.DisposeAsync(); }
    }
}
