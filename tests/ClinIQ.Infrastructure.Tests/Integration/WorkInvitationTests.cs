using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterDoctor;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class WorkInvitationTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private PremisesCorrectionTests Setup => new(fixture);
    [SqlServerFact]
    public async Task InviteBeforeAccountThenProfessionalApprovalCreatesOneManagedAssociationAtAcceptedFee()
    {
        var (manager, _, branch) = await Setup.Branch(); var address = NewEmail();
        var invitation = (await Issue(manager, WorkInvitationKind.Doctor, branch.Id, null, address, 125)).Value;
        Assert.Equal("DevelopmentFile", invitation.DeliveryStatus);
        var doctor = await CreateDoctor(address); Assert.True((await Respond(doctor, invitation)).IsError);
        await ApproveDoctor(doctor);
        Assert.Equal("Accepted", (await Respond(doctor, invitation)).Value.Status);
        var places = await Places(doctor); var place = Assert.Single(places.Items);
        Assert.Equal(branch.Id, place.ClinicBranchId); Assert.Equal(125, place.ConsultationFee); Assert.Null(place.CurrentSubmissionId); Assert.True(place.IsActive);
        Assert.Equal(InvitationErrors.Closed, Assert.Single((await Respond(doctor, (await Inv(doctor, invitation.Id)).Value)).Errors));
    }
    [SqlServerFact]
    public async Task SameDoctorAtTwoBranchesHasTwoFeesAndCannotEditManagedPriceOrPhysicalDetails()
    {
        var (m1, _, b1) = await Setup.Branch(); var (m2, _, b2) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var a = (await Issue(m1, WorkInvitationKind.Doctor, b1.Id, null, d.Email, 50)).Value; var b = (await Issue(m2, WorkInvitationKind.Doctor, b2.Id, null, d.Email, 0)).Value;
        Assert.True((await Respond(d.UserId, a)).IsSuccess); Assert.True((await Respond(d.UserId, b)).IsSuccess);
        var places = (await Places(d.UserId)).Items; Assert.Equal(2, places.Length); Assert.Equal(new decimal[] { 0, 50 }, places.Select(x => x.ConsultationFee).Order().ToArray());
        var place = places.First(); var edit = await fixture.AsAsync(d.UserId, p => p.GetRequiredService<IPracticeLocationService>().ChangeFeeAsync(place.Id, 5, "EGP", place.Version, default));
        Assert.Equal(PracticeLocationErrors.ManagedLocation, Assert.Single(edit.Errors));
        var correction = await fixture.AsAsync(d.UserId, p => p.GetRequiredService<IPremisesCorrectionService>().SubmitAsync(place.Id, false, new("Changed", "Address", null, null), "Correction", place.Version, [], default));
        Assert.Equal(PracticeLocationErrors.ManagedLocation, Assert.Single(correction.Errors));
    }
    [SqlServerFact]
    public async Task WrongRecipientOrSecretCannotReadOrAcceptAndDeclinePublishesNothing()
    {
        var (m, _, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture); var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var i = (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value;
        Assert.Equal(InvitationErrors.NotFound, Assert.Single((await Respond(other.UserId, i)).Errors));
        Assert.Equal(InvitationErrors.NotFound, Assert.Single((await Call(d.UserId, s => s.RespondAsync(i.Id, new string('A', 43), i.Version, true, default))).Errors));
        Assert.Equal(InvitationErrors.NotFound, Assert.Single((await Inv(other.UserId, i.Id)).Errors));
        Assert.Equal("Declined", (await Respond(d.UserId, i, false)).Value.Status); Assert.Empty((await Places(d.UserId)).Items);
    }
    [SqlServerFact]
    public async Task ParallelInvitesAndParallelResponsesEachHaveOneWinner()
    {
        var (m, _, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var issued = await Task.WhenAll(Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100), Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100));
        var i = Assert.Single(issued, x => x.IsSuccess).Value; Assert.Single(issued, x => x.IsError);
        var results = await Task.WhenAll(Respond(d.UserId, i), Respond(d.UserId, i)); Assert.Single(results, x => x.IsSuccess); Assert.Single(results, x => x.IsError);
        Assert.Single((await Places(d.UserId)).Items);
    }
    [SqlServerFact]
    public async Task ExpiryAndResendInvalidateOldLinkAndKeepTermsHistory()
    {
        var (m, _, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture); var i = (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value;
        Assert.Equal(InvitationErrors.Cooldown, Assert.Single((await Call(m, s => s.ResendAsync(i.Id, i.Version, default))).Errors));
        fixture.Database.Clock.UtcNow = i.ExpiresAtUtc; Assert.Equal(InvitationErrors.Expired, Assert.Single((await Respond(d.UserId, i)).Errors));
        var next = (await Call(m, s => s.ResendAsync(i.Id, i.Version, default))).Value;
        Assert.Equal(i.Id, next.ReplacesInvitationId); Assert.Equal(i.Terms, next.Terms); Assert.True((await Respond(d.UserId, i)).IsError);
        Assert.True((await Respond(d.UserId, next)).IsSuccess);
    }
    [SqlServerFact]
    public async Task LosingIssuerMembershipPreventsIssueAndAcceptance()
    {
        var (m, clinic, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture); var i = (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value;
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET IsActive=0 WHERE UserId={m} AND ClinicId={clinic}");
        Assert.Equal(InvitationErrors.Forbidden, Assert.Single((await Respond(d.UserId, i)).Errors));
        Assert.Equal(InvitationErrors.NotFound, Assert.Single((await Issue(m, WorkInvitationKind.Doctor, b.Id, null, NewEmail(), 100)).Errors));
    }
    [SqlServerFact]
    public async Task InvitationOnlyReceptionistSignupConfirmsEmailBeforeWebLoginAndGrantsNoScopeBeforeAcceptance()
    {
        var (m, _, b) = await Setup.Branch(); var address = NewEmail(); var i = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, address)).Value;
        var receipt = (await Register(i, address)).Value; Assert.True(receipt.EmailConfirmationRequired);
        Assert.True((await Login(address, SessionClient.Web)).IsError); await fixture.ConfirmAsync(receipt.UserId);
        Assert.Equal(new[] { "Receptionist" }, (await Login(address, SessionClient.Web)).Value.User.Roles);
        Assert.Empty((await Workforce(receipt.UserId, s => s.AuthorizedDoctorsAsync(default))).Value);
        Assert.True((await Login(address, SessionClient.Mobile)).IsError); Assert.True((await Respond(receipt.UserId, i)).IsSuccess);
        Assert.Single((await Workforce(receipt.UserId, s => s.ListAssignmentsAsync(default))).Value);
        await using var db = fixture.Database.CreateContext(); Assert.False(await db.DoctorProfiles.AnyAsync(x => x.UserId == receipt.UserId)); Assert.False(await db.PatientProfiles.AnyAsync(x => x.UserId == receipt.UserId));
    }
    [SqlServerFact]
    public async Task BranchReceptionCoversOnlyAffiliatedDoctorsAndRevocationTakesEffectWithoutLogout()
    {
        var (m, _, b) = await Setup.Branch(); var d1 = await PracticeLocationTestData.ApprovedDoctor(fixture); var d2 = await PracticeLocationTestData.ApprovedDoctor(fixture);
        foreach (var d in new[] { d1, d2 }) Assert.True((await Respond(d.UserId, (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value)).IsSuccess);
        var email = NewEmail(); var i = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, email)).Value; var r = (await Register(i, email)).Value; await fixture.ConfirmAsync(r.UserId); Assert.True((await Respond(r.UserId, i)).IsSuccess);
        var grant = (await Login(email, SessionClient.Web)).Value;
        var doctors = (await Workforce(r.UserId, s => s.AuthorizedDoctorsAsync(default))).Value; Assert.Equal(2, doctors.Count);
        var first = doctors[0]; Assert.Single((await Workforce(r.UserId, s => s.AuthorizedPlacesAsync(first.DoctorProfileId, default))).Value);
        var assignments = (await Workforce(r.UserId, s => s.ListAssignmentsAsync(default))).Value; var a = Assert.Single(assignments);
        Assert.True((await Workforce(m, s => s.RevokeAssignmentAsync(a.Id, a.Version, "Reception ended", default))).IsSuccess);
        Assert.Empty((await Workforce(r.UserId, s => s.AuthorizedDoctorsAsync(default))).Value);
        Assert.True((await Workforce(r.UserId, s => s.AuthorizedPlacesAsync(first.DoctorProfileId, default))).IsError);
        Assert.Null(await fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().AuthenticateAsync(grant.AccessToken, SessionClient.Web, default)));
    }
    [SqlServerFact]
    public async Task IndependentAssignmentsAreExplicitUnionAndNeverExpandByMatchingAddresses()
    {
        var (d1, p1) = await Setup.Place(); var (d2, p2) = await Setup.Place(); var (d3, _) = await Setup.Place();
        await using var db = fixture.Database.CreateContext(); var address = NewEmail();
        var first = (await Issue(d1, WorkInvitationKind.Receptionist, null, p1.Id, address)).Value; var receipt = (await Register(first, address)).Value; await fixture.ConfirmAsync(receipt.UserId); Assert.True((await Respond(receipt.UserId, first)).IsSuccess);
        var second = (await Issue(d2, WorkInvitationKind.Receptionist, null, p2.Id, address)).Value; Assert.True((await Respond(receipt.UserId, second)).IsSuccess);
        var visible = (await Workforce(receipt.UserId, s => s.AuthorizedDoctorsAsync(default))).Value; Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(await db.DoctorProfiles.Where(x => x.UserId == d3).Select(x => x.Id).SingleAsync(), visible.Select(x => x.DoctorProfileId));
        var a = (await Workforce(receipt.UserId, s => s.ListAssignmentsAsync(default))).Value.Single(x => x.LocationId == p1.Id);
        Assert.True((await Workforce(d2, s => s.RevokeAssignmentAsync(a.Id, a.Version, "Wrong doctor", default))).IsError);
        Assert.True((await Workforce(d1, s => s.RevokeAssignmentAsync(a.Id, a.Version, "One scope ended", default))).IsSuccess);
        Assert.Single((await Workforce(receipt.UserId, s => s.AuthorizedDoctorsAsync(default))).Value);
    }
    [SqlServerFact]
    public async Task ExistingPatientSignupPreservesPasswordAndMobilePatientRole()
    {
        var (m, _, b) = await Setup.Branch(); var patient = await fixture.UserAsync("Patient"); var i = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, patient.Email!)).Value;
        Assert.True((await Register(i, patient.Email!, "Wrong-password1!")).IsError); var receipt = (await Register(i, patient.Email!)).Value; Assert.Equal(patient.Id, receipt.UserId);
        Assert.Equal(new[] { "Patient" }, (await Login(patient.Email!, SessionClient.Mobile)).Value.User.Roles);
        Assert.Equal(new[] { "Receptionist" }, (await Login(patient.Email!, SessionClient.Web)).Value.User.Roles);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(patient.PasswordHash, (await db.Users.SingleAsync(x => x.Id == patient.Id)).PasswordHash);
    }
    [SqlServerFact]
    public async Task AcceptanceSaveFailureRollsBackInvitationRoleAndAssignment()
    {
        var (m, _, b) = await Setup.Branch(); var doctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var i = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, doctor.Email)).Value;
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_WorkTest_FailAssignment ON dbo.ReceptionistAssignments AFTER INSERT AS THROW 51039, 'Synthetic assignment failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Respond(doctor.UserId, i)); } finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_WorkTest_FailAssignment;"); }
        Assert.Equal("Pending", (await Inv(doctor.UserId, i.Id)).Value.Status); Assert.False(await db.ReceptionistAssignments.AnyAsync(x => x.SourceInvitationId == i.Id));
        Assert.False(await (from ur in db.UserRoles join role in db.Roles on ur.RoleId equals role.Id where ur.UserId == doctor.UserId && role.Name == "Receptionist" select ur).AnyAsync());
    }
    [SqlServerFact]
    public async Task ManagerFeeChangeCommitsWithDurableNoticeDespiteDeliveryFailureAndCanRetry()
    {
        var (m, _, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture); Assert.True((await Respond(d.UserId, (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value)).IsSuccess);
        var p = Assert.Single((await Places(d.UserId)).Items); var environment = fixture.Services.GetRequiredService<IWebHostEnvironment>(); var old = environment.EnvironmentName; FeeChangeDto notice;
        environment.EnvironmentName = "Production";
        try { notice = (await Workforce(m, s => s.ChangeFeeAsync(p.Id, 0, "EGP", p.Version, default))).Value; } finally { environment.EnvironmentName = old; }
        Assert.Equal("Unavailable", notice.DeliveryStatus); Assert.Equal(0, Assert.Single((await Places(d.UserId)).Items).ConsultationFee);
        fixture.Database.Clock.UtcNow = fixture.Database.Clock.UtcNow.AddMinutes(1);
        Assert.Equal("DevelopmentFile", (await Workforce(m, s => s.RetryFeeNoticeAsync(notice.Id, default))).Value.DeliveryStatus);
        Assert.Single((await Workforce(d.UserId, s => s.FeeHistoryAsync(p.Id, default))).Value);
        Assert.Equal(100, (await Inv(d.UserId, (await dbInvitation(p.Id)))).Value.ConsultationFee);
        async Task<Guid> dbInvitation(Guid id) { await using var db = fixture.Database.CreateContext(); return (await db.DoctorPracticeLocations.Where(x => x.Id == id).Select(x => x.SourceInvitationId).SingleAsync())!.Value; }
    }
    [SqlServerFact]
    public async Task FeeNoticeSaveFailureCannotLeaveAnUnnotifiedPriceWrite()
    {
        var (m, _, b) = await Setup.Branch(); var d = await PracticeLocationTestData.ApprovedDoctor(fixture); Assert.True((await Respond(d.UserId, (await Issue(m, WorkInvitationKind.Doctor, b.Id, null, d.Email, 100)).Value)).IsSuccess);
        var p = Assert.Single((await Places(d.UserId)).Items); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_WorkTest_FailFeeNotice ON dbo.PracticeFeeChanges AFTER INSERT AS THROW 51049, 'Synthetic fee notice failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Workforce(m, s => s.ChangeFeeAsync(p.Id, 20, "EGP", p.Version, default))); } finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_WorkTest_FailFeeNotice;"); }
        Assert.Equal(p, Assert.Single((await Places(d.UserId)).Items)); Assert.False(await db.PracticeFeeChanges.AnyAsync(x => x.LocationId == p.Id));
    }
    [SqlServerFact]
    public async Task RevokedAssignmentCanBeGrantedAgainWithoutDeletingEarlierHistory()
    {
        var (m, _, b) = await Setup.Branch(); var address = NewEmail(); var first = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, address)).Value; var r = (await Register(first, address)).Value; await fixture.ConfirmAsync(r.UserId); await Respond(r.UserId, first);
        var a = Assert.Single((await Workforce(r.UserId, s => s.ListAssignmentsAsync(default))).Value); await Workforce(m, s => s.RevokeAssignmentAsync(a.Id, a.Version, "Old assignment ended", default));
        var next = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, address)).Value; Assert.True((await Respond(r.UserId, next)).IsSuccess);
        var rows = (await Workforce(r.UserId, s => s.ListAssignmentsAsync(default))).Value; Assert.Equal(2, rows.Count); Assert.Single(rows, x => x.IsActive); Assert.Single(rows, x => !x.IsActive && x.RevocationReason == "Old assignment ended");
    }
    [SqlServerFact]
    public async Task SqlScopeConstraintsAndDownGuardProtectHistory()
    {
        var (m, _, b) = await Setup.Branch(); var i = (await Issue(m, WorkInvitationKind.Receptionist, b.Id, null, NewEmail())).Value;
        await using var db = fixture.Database.CreateContext(); var invalid = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorkInvitations SET BranchId=NULL WHERE Id={i.Id}")); Assert.Equal(547, invalid.Number);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028152615_WorkInvitations");
        try
        {
            var down = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028134115_PremisesCorrections")); Assert.Equal(51004, down.Number);
        }
        finally { await migrator.MigrateAsync(); }
    }
    private static string NewEmail() => $"work-{Guid.NewGuid():N}@example.test";
    internal Task<T> Call<T>(Guid? actor, Func<IWorkInvitationService, Task<T>> action) => fixture.AsAsync(actor, p => action(p.GetRequiredService<IWorkInvitationService>()));
    internal Task<T> Workforce<T>(Guid? actor, Func<IWorkforceService, Task<T>> action) => fixture.AsAsync(actor, p => action(p.GetRequiredService<IWorkforceService>()));
    internal Task<Result<InvitationDto>> Issue(Guid actor, WorkInvitationKind kind, Guid? branch, Guid? place, string email, decimal? fee = null) => Call(actor, s => s.IssueAsync(kind, branch, place, email, "Synthetic agreed terms", fee, fee.HasValue ? "EGP" : null, default));
    private Task<Result<InvitationDto>> Inv(Guid actor, Guid id) => Call(actor, s => s.GetAsync(id, true, default));
    internal Task<Result<InvitationDto>> Respond(Guid actor, InvitationDto row, bool accept = true) => Call(actor, s => s.RespondAsync(row.Id, Secret(row.Id), row.Version, accept, default));
    internal Task<Result<ReceptionistRegistrationReceipt>> Register(InvitationDto row, string email, string password = DoctorReviewFixture.Password) => fixture.AsAsync(null, p => p.GetRequiredService<IReceptionistRegistrationService>().RegisterAsync(row.Id, Secret(row.Id), "Synthetic receptionist", email, password, default));
    internal string Secret(Guid id)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.PrivateRoot, "development-work-messages"), "*.json"))
        { using var json = JsonDocument.Parse(File.ReadAllText(path)); if (json.RootElement.TryGetProperty("InvitationId", out var key) && key.GetGuid() == id) { var fragment = new Uri(json.RootElement.GetProperty("InvitationUrl").GetString()!).Fragment; return Uri.UnescapeDataString(fragment[(fragment.IndexOf("secret=", StringComparison.Ordinal) + 7)..]); } }
        throw new InvalidOperationException("Synthetic invitation delivery was not found.");
    }
    internal Task<PracticeLocationPage> Places(Guid doctor) => fixture.AsAsync(doctor, async p => (await p.GetRequiredService<IPracticeLocationService>().ListAsync(false, 1, 100, null, default)).Value);
    private Task<Result<SessionGrant>> Login(string email, SessionClient client) => fixture.AsAsync(null, p => p.GetRequiredService<ISessionService>().LoginAsync(email, DoctorReviewFixture.Password, false, client, default));
    private async Task<Guid> CreateDoctor(string email)
    {
        var files = new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }.Select(k => new RegistrationUpload(k, "synthetic.pdf", "application/pdf", DoctorReviewFixture.Evidence.Length, new MemoryStream(DoctorReviewFixture.Evidence))).ToArray();
        try { var receipt = await fixture.AsAsync(null, async p => (await p.GetRequiredService<IDoctorRegistrationService>().RegisterAsync(new RegisterDoctorCommand("Synthetic Doctor", email, DoctorReviewFixture.Password, "01012345678", "SYNTHETIC", fixture.SpecialtyId, ProfessionalGrade.GeneralPractitioner, files), default)).Value); await fixture.ConfirmAsync(receipt.UserId); return receipt.UserId; }
        finally { foreach (var f in files) await f.Content.DisposeAsync(); }
    }
    private Task<bool> ApproveDoctor(Guid user) => fixture.AsAsync(fixture.Admin.Id, async p => { var db = p.GetRequiredService<ClinIQ.Infrastructure.Data.AppDbContext>(); var id = await db.DoctorRegistrations.Where(x => x.UserId == user && x.IsCurrent).Select(x => x.Id).SingleAsync(); var service = p.GetRequiredService<IDoctorReviewService>(); var current = (await service.GetAsync(id, default)).Value; Assert.True((await service.ReviewAsync(id, true, "Verified", current.Version, default)).IsSuccess); return true; });
}
