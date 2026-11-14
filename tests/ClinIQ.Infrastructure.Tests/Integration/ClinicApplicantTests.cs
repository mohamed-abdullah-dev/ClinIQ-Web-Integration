using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Clinics;
using ClinIQ.Application.Features.Clinics.Commands;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class ClinicApplicantTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task CombinedRegistrationSavesPendingApplicationAndBlocksLoginUntilConfirmedAndApproved()
    {
        var email = NewEmail(); var receipt = (await Register(email)).Value;
        await using var db = fixture.Database.CreateContext(); var user = await db.Users.SingleAsync(x => x.Id == receipt.UserId);
        Assert.False(user.EmailConfirmed); Assert.Equal("PendingReview", receipt.Status);
        Assert.True(await db.ClinicRegistrationSubmissions.AnyAsync(x => x.Id == receipt.SubmissionId && x.RequestId == receipt.ApplicationId));
        Assert.False(await db.AuthSessions.AnyAsync(x => x.UserId == user.Id)); Assert.False(await db.DoctorProfiles.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await db.ClinicManagementMemberships.AnyAsync(x => x.UserId == user.Id));
        Assert.Equal(SessionErrors.EmailUnconfirmed, Assert.Single((await Login(email, SessionClient.Web)).Errors));
        await fixture.ConfirmAsync(user.Id);
        Assert.Equal(SessionErrors.ClinicPendingReview, Assert.Single((await Login(email, SessionClient.Web)).Errors));
        Assert.Equal(SessionErrors.ClientNotAllowed, Assert.Single((await Login(email, SessionClient.Mobile)).Errors));
        Assert.True((await Review(receipt, ClinicApplicationStatus.Approved)).IsSuccess);
        Assert.Equal(new[] { "ClinicManager" }, (await Login(email, SessionClient.Web)).Value.User.Roles);
    }
    [SqlServerFact]
    public async Task ExistingPatientSubmitsSameFormWithoutReplacingCredentialsOrGrantingWebAccess()
    {
        var patient = await fixture.UserAsync("Patient"); var receipt = (await Register(patient.Email!)).Value;
        Assert.Equal(patient.Id, receipt.UserId); Assert.False(receipt.EmailConfirmationRequired);
        Assert.Equal(SessionErrors.ClinicPendingReview, Assert.Single((await Login(patient.Email!, SessionClient.Web)).Errors));
        Assert.Equal(new[] { "Patient" }, (await Login(patient.Email!, SessionClient.Mobile)).Value.User.Roles);
        await using var db = fixture.Database.CreateContext(); var saved = await db.Users.SingleAsync(x => x.Id == patient.Id);
        Assert.Equal(patient.PasswordHash, saved.PasswordHash); Assert.Equal(patient.SecurityStamp, saved.SecurityStamp); Assert.Equal(patient.FullName, saved.FullName);
        Assert.Equal(ClinicErrors.DraftLocked, Assert.Single((await Register(patient.Email!)).Errors));
    }
    [SqlServerFact]
    public async Task PendingDoctorCannotBypassReviewThroughClinicApplicantRole()
    {
        var doctor = await fixture.DoctorAsync(); Assert.True((await Register(doctor.Email)).IsSuccess);
        Assert.Equal(SessionErrors.PendingReview, Assert.Single((await Login(doctor.Email, SessionClient.Web)).Errors));
    }
    [SqlServerTheory]
    [InlineData(ClinicApplicationStatus.Rejected)]
    [InlineData(ClinicApplicationStatus.NeedsInformation)]
    public async Task RejectedOrIncompleteApplicantResubmitsPublicFormWithoutSessionAndKeepsOldEvidence(ClinicApplicationStatus outcome)
    {
        var email = NewEmail(); var first = (await Register(email)).Value; await fixture.ConfirmAsync(first.UserId);
        Assert.True((await Review(first, outcome)).IsSuccess);
        Assert.Equal(outcome == ClinicApplicationStatus.Rejected ? SessionErrors.ClinicRejected : SessionErrors.ClinicNeedsInformation, Assert.Single((await Login(email, SessionClient.Web)).Errors));
        var second = (await Register(email, input: ClinicTestData.Input() with { LicensedOperator = "Corrected operator" })).Value;
        Assert.Equal(first.ApplicationId, second.ApplicationId); Assert.NotEqual(first.SubmissionId, second.SubmissionId);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(2, await db.ClinicRegistrationSubmissions.CountAsync(x => x.RequestId == first.ApplicationId));
        Assert.Equal(outcome, (await db.ClinicRegistrationSubmissions.SingleAsync(x => x.Id == first.SubmissionId)).Status);
        Assert.False(await db.AuthSessions.AnyAsync(x => x.UserId == first.UserId));
    }
    [SqlServerFact]
    public async Task PendingLegacyWebSessionAndLostMembershipCannotKeepApplicantAccess()
    {
        var email = NewEmail(); var receipt = (await Register(email)).Value; await fixture.ConfirmAsync(receipt.UserId);
        await using var db = fixture.Database.CreateContext(); var user = await db.Users.SingleAsync(x => x.Id == receipt.UserId);
        var access = SessionSecrets.Create(); var refresh = SessionSecrets.Create();
        var session = AuthSession.Create(user.Id, SessionClient.Web, user.SecurityStamp!, SessionSecrets.Hash(access), fixture.Database.Clock.GetUtcNow(), false);
        var proof = SessionRefreshToken.Create(session.Id, SessionSecrets.Hash(refresh)); db.AuthSessions.Add(session); db.SessionRefreshTokens.Add(proof); await db.SaveChangesAsync();
        Assert.Null(await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().AuthenticateAsync(SessionSecrets.Format(session.Id, access), SessionClient.Web, default)));
        Assert.True((await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().RefreshAsync(SessionSecrets.Format(proof.Id, refresh), SessionClient.Web, default))).IsError);
        Assert.True((await Review(receipt, ClinicApplicationStatus.Approved)).IsSuccess); var grant = (await Login(email, SessionClient.Web)).Value;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET IsActive=0 WHERE UserId={user.Id}");
        Assert.Null(await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().AuthenticateAsync(grant.AccessToken, SessionClient.Web, default)));
    }
    [SqlServerFact]
    public async Task InvalidPasswordCannotOverwriteSomeoneElsesApplicationAndUsesAccountLockout()
    {
        var email = NewEmail(); var receipt = (await Register(email)).Value;
        for (var i = 0; i < 5; i++) Assert.Equal(SessionErrors.InvalidCredentials, Assert.Single((await Register(email, "Wrong-password1!")).Errors));
        Assert.Equal(SessionErrors.InvalidCredentials, Assert.Single((await Register(email)).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.ClinicRegistrationSubmissions.CountAsync(x => x.RequestId == receipt.ApplicationId));
        Assert.True((await db.Users.SingleAsync(x => x.Id == receipt.UserId)).LockoutEnd.HasValue);
    }
    [SqlServerFact]
    public async Task InvalidEvidenceAndWeakPasswordDoNotLeaveOrphanAccounts()
    {
        var email = NewEmail(); Assert.True((await Register(email, "weak")).IsError);
        var invalid = NewEmail(); var files = ClinicTestData.Uploads();
        try { Assert.True((await fixture.AsAsync(null, p => p.GetRequiredService<IClinicApplicantService>().RegisterAsync("Name", invalid, DoctorReviewFixture.Password, ClinicTestData.Input(), files.Take(1).ToArray(), default))).IsError); }
        finally { foreach (var file in files) await file.Content.DisposeAsync(); }
        await using var db = fixture.Database.CreateContext(); Assert.False(await db.Users.AnyAsync(x => x.Email == email || x.Email == invalid));
    }
    [SqlServerFact]
    public async Task SubmissionFailureRollsBackAccountRoleAndFilesTogether()
    {
        var email = NewEmail(); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_IntakeTest_FailSubmission ON dbo.ClinicRegistrationSubmissions AFTER INSERT AS THROW 51029, 'Synthetic intake failure.', 1;");
        var before = Directory.Exists(fixture.PrivateRoot) ? Directory.GetFiles(fixture.PrivateRoot, "*.pdf", SearchOption.AllDirectories).Order().ToArray() : [];
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Register(email)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_IntakeTest_FailSubmission;"); }
        Assert.False(await db.Users.AnyAsync(x => x.Email == email));
        Assert.Equal(before, Directory.GetFiles(fixture.PrivateRoot, "*.pdf", SearchOption.AllDirectories).Order().ToArray());
    }
    [SqlServerFact]
    public async Task AdminHasWebAccessAndMobileExcludesAllAdministrativeRoles()
    {
        Assert.Equal(new[] { "Admin" }, (await Login(fixture.Admin.Email!, SessionClient.Web)).Value.User.Roles);
        Assert.Equal(SessionErrors.ClientNotAllowed, Assert.Single((await Login(fixture.Admin.Email!, SessionClient.Mobile)).Errors));
        var dual = await fixture.UserAsync("Patient");
        await fixture.AsAsync(null, async p => { var users = p.GetRequiredService<UserManager<AppUser>>(); Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(dual.Id.ToString()))!, "Admin")).Succeeded); return true; });
        Assert.Equal(new[] { "Patient" }, (await Login(dual.Email!, SessionClient.Mobile)).Value.User.Roles);
        Assert.Equal(new[] { "Admin" }, (await Login(dual.Email!, SessionClient.Web)).Value.User.Roles);
    }
    [SqlServerFact]
    public async Task LegacyMobileAdminCredentialsCannotAuthenticateOrRefresh()
    {
        var access = SessionSecrets.Create(); var refresh = SessionSecrets.Create();
        var session = AuthSession.Create(fixture.Admin.Id, SessionClient.Mobile, fixture.Admin.SecurityStamp!, SessionSecrets.Hash(access), fixture.Database.Clock.GetUtcNow(), false);
        var proof = SessionRefreshToken.Create(session.Id, SessionSecrets.Hash(refresh));
        await using (var db = fixture.Database.CreateContext()) { db.AuthSessions.Add(session); db.SessionRefreshTokens.Add(proof); await db.SaveChangesAsync(); }
        Assert.Null(await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().AuthenticateAsync(SessionSecrets.Format(session.Id, access), SessionClient.Mobile, default)));
        Assert.True((await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().RefreshAsync(SessionSecrets.Format(proof.Id, refresh), SessionClient.Mobile, default))).IsError);
    }
    private static string NewEmail() => $"applicant-{Guid.NewGuid():N}@example.test";
    private async Task<Result<ClinicApplicantReceipt>> Register(string email, string password = DoctorReviewFixture.Password, ClinicApplicationData? input = null)
    {
        var docs = ClinicTestData.Uploads();
        try { return await fixture.AsAsync(null, p => p.GetRequiredService<IClinicApplicantService>().RegisterAsync("Synthetic applicant", email, password, input ?? ClinicTestData.Input(), docs, default)); }
        finally { foreach (var doc in docs) await doc.Content.DisposeAsync(); }
    }
    private Task<Result<ClinicApplicationDto>> Review(ClinicApplicantReceipt r, ClinicApplicationStatus outcome) => fixture.AsAsync(fixture.Admin.Id, async p =>
    {
        var service = p.GetRequiredService<IClinicService>(); var current = (await service.GetAsync(r.ApplicationId, true, default)).Value.Application;
        return await service.ReviewAsync(r.ApplicationId, r.SubmissionId, outcome, "Synthetic intake review", current.Version, true, true, true, true, default);
    });
    private Task<Result<SessionGrant>> Login(string email, SessionClient client) => fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().LoginAsync(email, DoctorReviewFixture.Password, false, client, default));
}
