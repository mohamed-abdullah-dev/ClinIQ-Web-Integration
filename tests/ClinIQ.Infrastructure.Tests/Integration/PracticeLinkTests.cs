using System.Security.Claims;
using System.Text.Json;
using ClinIQ.Application.Features.PracticeLinks;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLinks;
using ClinIQ.Domain.Appointments;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLinkTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    internal Task<T> Call<T>(Guid? actor, Func<IPracticeLinkService, Task<T>> action, string client = "Web") => fixture.AsAsync(actor, p =>
    {
        (p.GetRequiredService<IHttpContextAccessor>().HttpContext?.User.Identity as ClaimsIdentity)?.AddClaim(new("client", client));
        return action(p.GetRequiredService<IPracticeLinkService>());
    });
    internal async Task<LinkRequestDto> Submit(Guid doctor, Guid location, Guid branch)
    {
        var source = await Call(doctor, s => s.PreviewAsync(location, branch, default)); Assert.True(source.IsSuccess, string.Join(",", source.Errors.Select(e => e.Code)));
        var v = source.Value;
        var result = await Call(doctor, s => s.CreateAsync(new(location, branch, v.LocationVersion, v.BranchVersion, true, true), default));
        Assert.True(result.IsSuccess, string.Join(",", result.Errors.Select(e => e.Code))); return result.Value;
    }
    internal Task<Result<LinkRequestDto>> Decide(Guid actor, LinkRequestDto r, bool accept = true) => Call(actor, s => s.DecideAsync(r.Id, new(accept, r.Version, "Explicit synthetic consent", true, true), default));
    private async Task<(Guid Doctor, Guid Location, Guid Manager, Guid Branch, LinkRequestDto Request)> Setup()
    {
        var p = await new SchedulingTests(fixture).Place(); var b = await new PremisesCorrectionTests(fixture).Branch();
        return (p.Doctor, p.Place.Id, b.Owner, b.Branch.Id, await Submit(p.Doctor, p.Place.Id, b.Branch.Id));
    }

    [SqlServerFact]
    public async Task ActualLinkPreservesIdentityEvidenceBookingsAndSeparatesOldAndNewReceptionScopes()
    {
        var attendance = new AttendanceTests(fixture); var x = await attendance.Setup(); var branch = await new PremisesCorrectionTests(fixture).Branch();
        var invitations = new WorkInvitationTests(fixture); var staff = await fixture.UserAsync("Receptionist");
        var invite = (await invitations.Issue(branch.Owner, WorkInvitationKind.Receptionist, branch.Branch.Id, null, staff.Email!)).Value;
        Assert.True((await invitations.Respond(staff.Id, invite)).IsSuccess);
        await using var db = fixture.Database.CreateContext(); var before = await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == x.Session.LocationId);
        var oldBooking = JsonSerializer.Serialize(await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == x.Rows[0].Id));
        var oldSession = JsonSerializer.Serialize(await db.BookingSessions.AsNoTracking().SingleAsync(s => s.Id == x.Session.Id));
        var request = await Submit(x.Doctor, before.Id, branch.Branch.Id);
        Assert.Null((await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == before.Id)).ClinicBranchId);
        Assert.True((await Decide(branch.Owner, request)).IsSuccess);
        var after = await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == before.Id);
        Assert.Equal(branch.Branch.Id, after.ClinicBranchId); Assert.Equal(request.Id, after.SourceLinkId); Assert.Null(after.SourceInvitationId);
        Assert.Equal(before.CurrentSubmissionId, after.CurrentSubmissionId); Assert.Equal(before.AttemptNumber, after.AttemptNumber);
        Assert.Equal(before.Name, after.Name); Assert.Equal(before.Address, after.Address); Assert.Equal(before.ConsultationFee, after.ConsultationFee);
        Assert.Equal(oldBooking, JsonSerializer.Serialize(await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == x.Rows[0].Id)));
        Assert.Equal(oldSession, JsonSerializer.Serialize(await db.BookingSessions.AsNoTracking().SingleAsync(s => s.Id == x.Session.Id)));
        Assert.True((await attendance.Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).IsSuccess);
        Assert.True((await attendance.Call(staff.Id, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).IsError);
        var schedule = new SchedulingTests(fixture); var future = (await schedule.Extra(x.Doctor, before.Id, schedule.Tomorrow, 18, 20)).Value;
        Assert.Equal(branch.Branch.Id, (await db.BookingSessions.SingleAsync(s => s.Id == future.Id)).OperationalBranchId);
        Assert.True((await attendance.Call(staff.Id, s => s.QueueAsync(x.Session.DoctorProfileId, future.Id, 1, 20, default))).IsSuccess);
        Assert.True((await attendance.Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, future.Id, 1, 20, default))).IsError);
        Assert.Null(Assert.Single((await invitations.Workforce(x.Receptionist, s => s.AuthorizedPlacesAsync(x.Session.DoctorProfileId, default))).Value).BranchId);
        Assert.Equal(branch.Branch.Id, Assert.Single((await invitations.Workforce(staff.Id, s => s.AuthorizedPlacesAsync(x.Session.DoctorProfileId, default))).Value).BranchId);
        var assignment = Assert.Single((await invitations.Workforce(x.Doctor, s => s.ListAssignmentsAsync(default))).Value);
        Assert.True((await invitations.Workforce(branch.Owner, s => s.RevokeAssignmentAsync(assignment.Id, assignment.Version, "Not mine", default))).IsError);
        Assert.True((await invitations.Workforce(x.Doctor, s => s.RevokeAssignmentAsync(assignment.Id, assignment.Version, "Doctor revokes retained scope", default))).IsSuccess);
        Assert.True((await attendance.Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).IsError);
        Assert.True((await fixture.AsAsync(x.Doctor, p => p.GetRequiredService<IPracticeLocationService>().ChangeFeeAsync(after.Id, 200, "EGP", Convert.ToBase64String(after.Version), default))).IsError);
        Assert.True((await invitations.Workforce(branch.Owner, s => s.ChangeFeeAsync(after.Id, 200, "EGP", Convert.ToBase64String(after.Version), default))).IsSuccess);
        Assert.Equal(oldBooking, JsonSerializer.Serialize(await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == x.Rows[0].Id)));
    }
    [SqlServerFact]
    public async Task WrongRolesClientsOwnersAndMembershipCannotReadOrActOnLinks()
    {
        var x = await Setup(); var other = await new PremisesCorrectionTests(fixture).Branch();
        Assert.True((await Call(null, s => s.GetAsync(x.Request.Id, default))).IsError);
        Assert.True((await Call(x.Doctor, s => s.GetAsync(x.Request.Id, default), "Mobile")).IsError);
        Assert.True((await Call(fixture.Admin.Id, s => s.GetAsync(x.Request.Id, default))).IsError);
        Assert.True((await Call(other.Owner, s => s.GetAsync(x.Request.Id, default))).IsError);
        Assert.True((await Decide(other.Owner, x.Request)).IsError); Assert.True((await Decide(x.Doctor, x.Request)).IsError);
        Assert.True((await Call(x.Manager, s => s.WithdrawAsync(x.Request.Id, new(x.Request.Version, "Not doctor"), default))).IsError);
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicManagementMemberships SET IsActive=0 WHERE UserId={x.Manager}");
        Assert.True((await Decide(x.Manager, x.Request)).IsError);
        Assert.Null((await db.DoctorPracticeLocations.SingleAsync(p => p.Id == x.Location)).ClinicBranchId);
    }
    [SqlServerTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedPlaceOrBranchRequiresFreshDoctorConsentButWithdrawalStillWorks(bool branch)
    {
        var x = await Setup(); await using var db = fixture.Database.CreateContext();
        if (branch) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET Address=N'Updated same-premises text' WHERE Id={x.Branch}");
        else await fixture.AsAsync(x.Doctor, p => p.GetRequiredService<IPracticeLocationService>().ChangeFeeAsync(x.Location, 150, "EGP", x.Request.Source.LocationVersion, default));
        Assert.Equal(PracticeLinkErrors.Stale, Assert.Single((await Decide(x.Manager, x.Request)).Errors));
        Assert.True((await Call(x.Doctor, s => s.WithdrawAsync(x.Request.Id, new(x.Request.Version, "Refresh consent"), default))).IsSuccess);
        var next = await Submit(x.Doctor, x.Location, x.Branch); Assert.NotEqual(x.Request.Id, next.Id);
        Assert.True((await Decide(x.Manager, next)).IsSuccess);
    }
    [SqlServerTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task PendingDepartureOrClosureCannotBeBypassedByLinking(bool branch)
    {
        var x = await Setup(); var lifecycle = new PracticeLifecycleTests(fixture);
        var scope = (await lifecycle.Get(branch ? x.Manager : x.Doctor, branch ? x.Branch : x.Location, branch)).Value;
        Assert.True((await lifecycle.Change(branch ? x.Manager : x.Doctor, scope)).IsSuccess);
        Assert.Equal(PracticeLinkErrors.Unavailable, Assert.Single((await Decide(x.Manager, x.Request)).Errors));
        Assert.True((await Decide(x.Manager, x.Request, false)).IsSuccess);
    }
    [SqlServerFact]
    public async Task DuplicateInvitationAssociationPreventsMergingButRequestCanBeRejected()
    {
        var x = await Setup(); var helper = new WorkInvitationTests(fixture); await using var db = fixture.Database.CreateContext();
        var email = await db.Users.Where(u => u.Id == x.Doctor).Select(u => u.Email).SingleAsync();
        var invite = (await helper.Issue(x.Manager, WorkInvitationKind.Doctor, x.Branch, null, email!, 100)).Value;
        Assert.True((await helper.Respond(x.Doctor, invite)).IsSuccess);
        Assert.Equal(PracticeLinkErrors.Duplicate, Assert.Single((await Decide(x.Manager, x.Request)).Errors));
        Assert.True((await Decide(x.Manager, x.Request, false)).IsSuccess);
        Assert.Null((await db.DoctorPracticeLocations.SingleAsync(p => p.Id == x.Location)).ClinicBranchId);
    }
    [SqlServerFact]
    public async Task ConcurrentCreateAndAcceptAreIdempotentAndConflictingDecisionCannotOverwriteConsent()
    {
        var p = await new SchedulingTests(fixture).Place(); var b = await new PremisesCorrectionTests(fixture).Branch();
        var requests = await Task.WhenAll(Submit(p.Doctor, p.Place.Id, b.Branch.Id), Submit(p.Doctor, p.Place.Id, b.Branch.Id));
        Assert.Equal(requests[0].Id, requests[1].Id);
        var decisions = await Task.WhenAll(Decide(b.Owner, requests[0]), Decide(b.Owner, requests[1]));
        Assert.All(decisions, r => Assert.True(r.IsSuccess));
        Assert.Equal(PracticeLinkErrors.Closed, Assert.Single((await Decide(b.Owner, requests[0], false)).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.DoctorPracticeLocations.CountAsync(p => p.ClinicBranchId == b.Branch.Id));
    }
    [SqlServerFact]
    public async Task PublicationRacingWithAcceptanceCapturesOneCoherentOperationalScope()
    {
        var x = await Setup(); var helper = new SchedulingTests(fixture);
        var published = helper.Extra(x.Doctor, x.Location, helper.Tomorrow, 18, 20); var linked = Decide(x.Manager, x.Request);
        await Task.WhenAll(published, linked); Assert.True(published.Result.IsSuccess); Assert.True(linked.Result.IsSuccess);
        await using var db = fixture.Database.CreateContext(); var row = await db.BookingSessions.SingleAsync(s => s.Id == published.Result.Value.Id);
        Assert.True(row.OperationalBranchId is null || row.OperationalBranchId == x.Branch);
        var next = (await helper.Extra(x.Doctor, x.Location, helper.Tomorrow.AddDays(1), 18, 20)).Value;
        Assert.Equal(x.Branch, (await db.BookingSessions.SingleAsync(s => s.Id == next.Id)).OperationalBranchId);
    }
    [SqlServerFact]
    public async Task ConsentAndProvenanceAreProtectedBySqlAndRollbackGuard()
    {
        var x = await Setup(); await using var db = fixture.Database.CreateContext();
        Assert.Equal(51022, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PracticeLinkRequests SET LocationName=N'Rewrite' WHERE Id={x.Request.Id}"))).Number);
        Assert.Equal(51022, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PracticeLinkRequests WHERE Id={x.Request.Id}"))).Number);
        Assert.True((await Decide(x.Manager, x.Request)).IsSuccess);
        Assert.Equal(51022, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PracticeLinkRequests SET DecisionReason=N'Changed' WHERE Id={x.Request.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET SourceLinkId=NULL WHERE Id={x.Location}"))).Number);
        Assert.Equal(51023, (await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261030173034_PracticeLifecycle"))).Number);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
    [SqlServerFact]
    public async Task FailedAssociationSaveRollsBackManagerConsent()
    {
        var x = await Setup(); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_LinkTest_Fail ON dbo.DoctorPracticeLocations AFTER UPDATE AS THROW 51079, 'Synthetic link failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Decide(x.Manager, x.Request)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_LinkTest_Fail;"); }
        Assert.Equal(PracticeLinkStatus.Pending, (await db.PracticeLinkRequests.SingleAsync(r => r.Id == x.Request.Id)).Status);
        Assert.Null((await db.DoctorPracticeLocations.SingleAsync(p => p.Id == x.Location)).ClinicBranchId);
        Assert.True((await Decide(x.Manager, x.Request)).IsSuccess);
    }
}
