using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Application.Features.DoctorReviews.Commands;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorReviewTests : IAsyncLifetime
{
    private readonly DoctorReviewFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();
    private Task<T> Admin<T>(Func<IDoctorReviewService, Task<T>> work) =>
        _fixture.AsAsync(_fixture.Admin.Id, provider => work(provider.GetRequiredService<IDoctorReviewService>()));
    private async Task<string> Version(Guid id) => (await Admin(service => service.GetAsync(id, default))).Value.Version;
    private Task<Result<DoctorReviewReceipt>> Decide(Guid id, bool approve, string version) =>
        Admin(service => service.ReviewAsync(id, approve, "Synthetic evidence reviewed", version, default));

    [SqlServerFact]
    public async Task ApprovalCreatesVerifiedProfileRoleAndReviewInOneCommitWithTrustedAudit()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId; var version = await Version(id);
        var result = await Decide(id, true, version); Assert.True(result.IsSuccess); Assert.NotEqual(version, result.Value.Version);
        await using var db = _fixture.Database.CreateContext();
        var registration = await db.DoctorRegistrations.SingleAsync(); var review = await db.DoctorRegistrationReviews.SingleAsync();
        var profile = await db.DoctorProfiles.SingleAsync();
        Assert.Equal(DoctorRegistrationStatus.Approved, registration.Status);
        Assert.Equal(_fixture.Admin.Id, review.ReviewerUserId); Assert.Equal(_fixture.Database.Clock.GetUtcNow(), review.ReviewedAtUtc);
        Assert.Equal(_fixture.Admin.Id, registration.LastModifiedByUserId); Assert.Null(registration.CreatedByUserId);
        Assert.Equal(_fixture.Admin.Id, profile.CreatedByUserId); Assert.Equal(registration.SpecialtyId, profile.SpecialtyId);
        Assert.Equal(registration.UserId, profile.UserId); Assert.Equal(registration.Id, profile.DoctorRegistrationId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().IsInRoleAsync((await db.Users.SingleAsync(user => user.Id == doctor.Receipt.UserId)), "Doctor"));
    }

    [SqlServerFact]
    public async Task ApprovalDoesNotConfirmEmailAndDoctorCanLoginWebOnlyAfterConfirmation()
    {
        var doctor = await _fixture.DoctorAsync(false); var id = doctor.Receipt.RegistrationId;
        Assert.True((await Decide(id, true, await Version(id))).IsSuccess);
        async Task<Result<SessionGrant>> Login(SessionClient client) => await _fixture.AsAsync<Result<SessionGrant>>(null,
            provider => provider.GetRequiredService<ISessionService>().LoginAsync(doctor.Email, DoctorReviewFixture.Password, false, client, default));
        Assert.Equal(SessionErrors.EmailUnconfirmed, Assert.Single((await Login(SessionClient.Web)).Errors));
        await _fixture.ConfirmAsync(doctor.Receipt.UserId);
        Assert.True((await Login(SessionClient.Web)).IsSuccess);
        Assert.Equal(SessionErrors.ClientNotAllowed, Assert.Single((await Login(SessionClient.Mobile)).Errors));
    }

    [SqlServerFact]
    public async Task RejectionGrantsNoDoctorProfileOrRoleAndLoginKeepsPasswordPrivacy()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        Assert.True((await Decide(id, false, await Version(id))).IsSuccess);
        await using var db = _fixture.Database.CreateContext();
        Assert.Empty(await db.DoctorProfiles.ToArrayAsync());
        Assert.Empty(await db.UserRoles.Where(row => row.UserId == doctor.Receipt.UserId).ToArrayAsync());
        Assert.Equal(DoctorRegistrationStatus.Rejected, (await db.DoctorRegistrationReviews.SingleAsync()).Outcome);
        await using var scope = _fixture.Services.CreateAsyncScope(); var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.Equal(SessionErrors.DoctorRejected, Assert.Single((await sessions.LoginAsync(doctor.Email, DoctorReviewFixture.Password, false, SessionClient.Web, default)).Errors));
        Assert.Equal(SessionErrors.InvalidCredentials, Assert.Single((await sessions.LoginAsync(doctor.Email, "Wrong1!", false, SessionClient.Web, default)).Errors));
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Patient") { Id = Guid.NewGuid() })).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(doctor.Receipt.UserId.ToString()))!, "Patient")).Succeeded);
        Assert.True((await sessions.LoginAsync(doctor.Email, DoctorReviewFixture.Password, false, SessionClient.Mobile, default)).IsSuccess);
    }

    [SqlServerFact]
    public async Task RepeatedAndOppositeDecisionsDoNotChangeFinalDecision()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId; var version = await Version(id);
        Assert.True((await Decide(id, true, version)).IsSuccess);
        Assert.Equal(DoctorReviewErrors.AlreadyReviewed, Assert.Single((await Decide(id, false, version)).Errors));
        Assert.Equal(DoctorReviewErrors.AlreadyReviewed, Assert.Single((await Decide(id, true, version)).Errors));
        await using var db = _fixture.Database.CreateContext(); Assert.Single(await db.DoctorRegistrationReviews.ToArrayAsync());
        Assert.Equal(DoctorRegistrationStatus.Approved, (await db.DoctorRegistrations.SingleAsync()).Status);
    }

    [SqlServerFact]
    public async Task ConflictingConcurrentDecisionsHaveOneWinner()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId; var version = await Version(id);
        var decisions = await Task.WhenAll(Decide(id, true, version), Decide(id, false, version));
        Assert.Single(decisions, result => result.IsSuccess);
        Assert.Equal(DoctorReviewErrors.AlreadyReviewed, Assert.Single(decisions.Single(result => result.IsError).Errors));
        await using var db = _fixture.Database.CreateContext(); Assert.Single(await db.DoctorRegistrationReviews.ToArrayAsync());
        var approved = (await db.DoctorRegistrations.SingleAsync()).Status == DoctorRegistrationStatus.Approved;
        Assert.Equal(approved ? 1 : 0, await db.DoctorProfiles.CountAsync());
        Assert.Equal(approved ? 1 : 0, await db.UserRoles.CountAsync(row => row.UserId == doctor.Receipt.UserId));
    }

    [SqlServerFact]
    public async Task DifferentApprovalsShareFirstDoctorRoleSafely()
    {
        var first = await _fixture.DoctorAsync(); var second = await _fixture.DoctorAsync();
        var firstVersion = await Version(first.Receipt.RegistrationId); var secondVersion = await Version(second.Receipt.RegistrationId);
        var results = await Task.WhenAll(Decide(first.Receipt.RegistrationId, true, firstVersion), Decide(second.Receipt.RegistrationId, true, secondVersion));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        await using var db = _fixture.Database.CreateContext(); Assert.Equal(2, await db.DoctorProfiles.CountAsync());
        Assert.Single(await db.Roles.Where(role => role.Name == "Doctor").ToArrayAsync());
    }

    [SqlServerFact]
    public async Task StaleVersionAndMissingReasonLeavePendingRequestUntouched()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        Assert.Equal(DoctorReviewErrors.Stale, Assert.Single((await Decide(id, true, Convert.ToBase64String(new byte[8]))).Errors));
        var result = await _fixture.AsAsync(_fixture.Admin.Id, provider => provider.GetRequiredService<ISender>()
            .Send(new ReviewDoctorRegistrationCommand(id, true, "", Convert.ToBase64String(new byte[8]))));
        Assert.True(result.IsError);
        await using var db = _fixture.Database.CreateContext(); Assert.Equal(DoctorRegistrationStatus.PendingReview, (await db.DoctorRegistrations.SingleAsync()).Status);
        Assert.Empty(await db.DoctorProfiles.ToArrayAsync()); Assert.Empty(await db.DoctorRegistrationReviews.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task NonAdminAndAnonymousServicesCannotReadDownloadOrReview()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId; var detail = (await Admin(service => service.GetAsync(id, default))).Value;
        foreach (var actor in new Guid?[] { null, doctor.Receipt.UserId })
            await _fixture.AsAsync(actor, async provider =>
            {
                var service = provider.GetRequiredService<IDoctorReviewService>();
                Assert.Equal(DoctorReviewErrors.Forbidden, Assert.Single((await service.ListAsync(1, 20, null, default)).Errors));
                Assert.Equal(DoctorReviewErrors.Forbidden, Assert.Single((await service.GetAsync(id, default)).Errors));
                Assert.Equal(DoctorReviewErrors.Forbidden, Assert.Single((await service.DownloadAsync(id, detail.Documents[0].Id, default)).Errors));
                Assert.Equal(DoctorReviewErrors.Forbidden, Assert.Single((await service.ReviewAsync(id, true, "Reason", detail.Version, default)).Errors));
                return true;
            });
        await using var db = _fixture.Database.CreateContext(); Assert.Empty(await db.DoctorEvidenceAccesses.ToArrayAsync());
        Assert.Empty(await db.DoctorRegistrationReviews.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task AdminCannotApproveTheirOwnRegistration()
    {
        var doctor = await _fixture.DoctorAsync();
        await using (var db = _fixture.Database.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorRegistrations SET UserId={_fixture.Admin.Id} WHERE Id={doctor.Receipt.RegistrationId}");
        Assert.Equal(DoctorReviewErrors.SelfReview, Assert.Single((await Decide(doctor.Receipt.RegistrationId, true, await Version(doctor.Receipt.RegistrationId))).Errors));
    }

    [SqlServerFact]
    public async Task AssignmentFailureRollsBackApprovalProfileRoleAndHistory()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId; var version = await Version(id);
        await using (var db = _fixture.Database.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailSyntheticDoctorAssignment ON AspNetUserRoles AFTER INSERT AS
                THROW 51003, 'Synthetic doctor assignment failure.', 1;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await Decide(id, true, version));
        await using var check = _fixture.Database.CreateContext();
        Assert.Equal(DoctorRegistrationStatus.PendingReview, (await check.DoctorRegistrations.SingleAsync()).Status);
        Assert.Empty(await check.DoctorProfiles.ToArrayAsync()); Assert.Empty(await check.DoctorRegistrationReviews.ToArrayAsync());
        Assert.Empty(await check.Roles.Where(role => role.Name == "Doctor").ToArrayAsync());
    }

    [SqlServerFact]
    public async Task DownloadChecksParentAndIntegrityAndAuditsGrantWithoutExposingStorageKeys()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        var detail = (await Admin(service => service.GetAsync(id, default))).Value;
        var document = detail.Documents[0];
        var missing = await Admin(service => service.DownloadAsync(Guid.NewGuid(), document.Id, default));
        Assert.Equal(DoctorReviewErrors.NotFound, Assert.Single(missing.Errors));
        var result = await Admin(service => service.DownloadAsync(id, document.Id, default)); Assert.True(result.IsSuccess);
        await using (var stream = result.Value.Content)
        { using var memory = new MemoryStream(); await stream.CopyToAsync(memory); Assert.Equal(DoctorReviewFixture.Evidence, memory.ToArray()); }
        await using var db = _fixture.Database.CreateContext(); var grant = await db.DoctorEvidenceAccesses.SingleAsync();
        Assert.Equal(_fixture.Admin.Id, grant.ViewerUserId); Assert.Equal(_fixture.Database.Clock.GetUtcNow(), grant.GrantedAtUtc);
        var key = (await db.Set<DoctorEvidenceDocument>().SingleAsync(row => row.Id == document.Id)).StorageKey;
        var json = JsonSerializer.Serialize(detail); Assert.DoesNotContain(key, json); Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("StorageKey", json);
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrTamperedFilesReturnUnavailableWithoutGrant(bool tamper)
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        await using var db = _fixture.Database.CreateContext(); var document = await db.Set<DoctorEvidenceDocument>().FirstAsync();
        var path = Path.Combine(_fixture.PrivateRoot, "doctor-evidence", document.StorageKey);
        if (tamper) await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'x', (int)document.Length).ToArray());
        else File.Move(path, path + ".synthetic-missing");
        Assert.Equal(DoctorReviewErrors.EvidenceUnavailable, Assert.Single((await Admin(service => service.DownloadAsync(id, document.Id, default))).Errors));
        Assert.Empty(await db.DoctorEvidenceAccesses.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task AccessAuditFailureDoesNotLeakAnOpenFile()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        await using var db = _fixture.Database.CreateContext(); var document = await db.Set<DoctorEvidenceDocument>().FirstAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailSyntheticEvidenceAudit ON DoctorEvidenceAccesses AFTER INSERT AS
            THROW 51004, 'Synthetic audit failure.', 1;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await Admin(service => service.DownloadAsync(id, document.Id, default)));
        using var exclusive = new FileStream(Path.Combine(_fixture.PrivateRoot, "doctor-evidence", document.StorageKey), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(await db.DoctorEvidenceAccesses.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task DatabaseRowVersionRejectsAnOutdatedEfWriter()
    {
        var doctor = await _fixture.DoctorAsync(); var id = doctor.Receipt.RegistrationId;
        await using var first = _fixture.Database.CreateContext(); await using var second = _fixture.Database.CreateContext();
        var one = await first.DoctorRegistrations.SingleAsync(); var two = await second.DoctorRegistrations.SingleAsync();
        Assert.True(one.Review(false, "First decision", _fixture.Admin.Id, _fixture.Database.Clock.GetUtcNow()).IsSuccess);
        await first.SaveChangesAsync();
        Assert.True(two.Review(true, "Outdated decision", _fixture.Admin.Id, _fixture.Database.Clock.GetUtcNow()).IsSuccess);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [SqlServerFact]
    public async Task ListFiltersAndPagesAndMissingIdsReturnNotFound()
    {
        var first = await _fixture.DoctorAsync(); await _fixture.DoctorAsync();
        Assert.True((await Decide(first.Receipt.RegistrationId, false, await Version(first.Receipt.RegistrationId))).IsSuccess);
        var page = (await Admin(service => service.ListAsync(1, 1, null, default))).Value;
        Assert.Equal(2, page.TotalCount); Assert.Single(page.Items);
        var pending = (await Admin(service => service.ListAsync(1, 20, DoctorRegistrationStatus.PendingReview, default))).Value;
        Assert.Single(pending.Items); Assert.Equal("PendingReview", pending.Items[0].Status);
        Assert.Equal(DoctorReviewErrors.NotFound, Assert.Single((await Admin(service => service.GetAsync(Guid.NewGuid(), default))).Errors));
    }

    [SqlServerFact]
    public async Task MigrationUpgradesAnExistingSubmissionWithoutChangingItsData()
    {
        var doctor = await _fixture.DoctorAsync(false);
        string passwordHash; string[] documentKeys;
        await using (var db = _fixture.Database.CreateContext())
        {
            passwordHash = (await db.Users.SingleAsync(row => row.Id == doctor.Receipt.UserId)).PasswordHash!;
            documentKeys = await db.Set<DoctorEvidenceDocument>().OrderBy(row => row.Id).Select(row => row.StorageKey).ToArrayAsync();
            // This is the fixture's own fresh database only; all new review tables are empty.
            await db.GetService<IMigrator>().MigrateAsync("20261025211857_AuthSessions");
        }
        await using (var migration = _fixture.Database.CreateContext()) await migration.Database.MigrateAsync();
        await using var reader = _fixture.Database.CreateContext();
        var registration = await reader.DoctorRegistrations.SingleAsync();
        Assert.Equal(doctor.Receipt.RegistrationId, registration.Id); Assert.Equal(doctor.Receipt.UserId, registration.UserId);
        Assert.Equal(DoctorRegistrationStatus.PendingReview, registration.Status); Assert.Equal(8, registration.Version.Length);
        Assert.Equal("SYNTHETIC", registration.RegistrationNumber);
        var user = await reader.Users.SingleAsync(row => row.Id == doctor.Receipt.UserId);
        Assert.Equal(passwordHash, user.PasswordHash); Assert.False(user.EmailConfirmed);
        Assert.Equal(documentKeys, await reader.Set<DoctorEvidenceDocument>().OrderBy(row => row.Id).Select(row => row.StorageKey).ToArrayAsync());
        Assert.Single(await reader.Specialties.ToArrayAsync()); Assert.Empty(await reader.DoctorProfiles.ToArrayAsync());
        Assert.Empty(await reader.DoctorRegistrationReviews.ToArrayAsync());
    }
}
