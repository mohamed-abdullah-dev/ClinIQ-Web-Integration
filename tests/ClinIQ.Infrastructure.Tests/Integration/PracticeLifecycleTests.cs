using System.Security.Claims;
using System.Text.Json;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.PracticeLifecycle;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLifecycle;
using ClinIQ.Domain.PracticeLocations;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLifecycleTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private readonly AttendanceTests attendance = new(fixture);
    internal Task<T> Call<T>(Guid? actor, Func<IPracticeLifecycleService, Task<T>> action, string client = "Web") => fixture.AsAsync(actor, p =>
    {
        (p.GetRequiredService<IHttpContextAccessor>().HttpContext?.User.Identity as ClaimsIdentity)?.AddClaim(new("client", client));
        return action(p.GetRequiredService<IPracticeLifecycleService>());
    });
    internal Task<Result<LifecycleScopeDto>> Get(Guid actor, Guid id, bool branch = false) => Call(actor, s => s.GetAsync(branch ? OperationalScopeKind.Branch : OperationalScopeKind.Location, id, default));
    internal Task<Result<LifecycleScopeDto>> Change(Guid actor, LifecycleScopeDto scope, bool finish = false) =>
        Call(actor, s => s.ChangeAsync(scope.Kind, scope.Id, finish, new(scope.Version, "Synthetic lifecycle reason"), default));
    internal Task<Result<EmergencySummary>> Submit(Guid actor, LifecycleScopeDto scope) => Call(actor,
        s => s.RequestAsync(scope.Kind, scope.Id, new(scope.Version, EmergencyReasonCategory.UnusablePremises, "Synthetic premises disruption"), default));
    internal Task<Result<EmergencyDetail>> Detail(Guid id) => Call(fixture.Admin.Id, s => s.DetailAsync(id, true, 1, 100, default));
    internal Task<Result<EmergencySummary>> Decide(EmergencyDetail d, bool approve = true) => Call(fixture.Admin.Id,
        s => s.DecideAsync(d.Request.Id, new(approve, d.Request.Version, "Synthetic independent Admin decision", d.Scope.Version, d.PreviewHash), default));
    internal async Task<AttendanceTests.Scenario> Setup(int count = 1, bool managed = false)
    {
        var s = await attendance.Setup(count, managed); await using var db = fixture.Database.CreateContext();
        var byUser = await db.Appointments.Where(a => a.SessionId == s.Session.Id).ToDictionaryAsync(a => a.PatientUserId, a => a.Id);
        var currentSession = (await new SchedulingTests(fixture).Call(s.Doctor, x => x.GetSessionAsync(s.Session.Id, default))).Value;
        return s with { Session = currentSession, Rows = s.Patients.Select(p => s.Rows.Single(r => r.Id == byUser[p])).ToArray() };
    }
    private async Task<AttendanceAppointment> CheckIn(AttendanceTests.Scenario s, int index) =>
        (await attendance.Write(s.Doctor, s, s.Rows[index], AttendanceAction.CheckIn)).Value.Appointment;
    private async Task<AttendanceAppointment> Complete(AttendanceTests.Scenario s, int index)
    {
        var r = await CheckIn(s, index); fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(30);
        return (await attendance.Write(s.Doctor, s, r, AttendanceAction.Complete, completed: s.Session.StartsAtUtc.AddMinutes(10))).Value.Appointment;
    }

    [SqlServerFact]
    public async Task PendingDepartureBlocksOldQuotesNewPublicationAndResumeButRetainsOperationalAccess()
    {
        var s = await Setup(); var extraPatient = await new AppointmentTests(fixture).Patient();
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(-10);
        var helper = new AppointmentTests(fixture); var accepted = AppointmentTests.Accept((await helper.Preview(extraPatient, s.Session.Id)).Value);
        var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        Assert.Equal("PendingDeparture", pending.State); Assert.True(pending.IsActive); Assert.False(pending.AcceptingNewBookings);
        Assert.Equal(1, pending.ConfirmedCount); Assert.False(pending.CanFinalize);
        Assert.True((await helper.Call(extraPatient, x => x.BookAsync(accepted, default))).IsError);
        var schedule = new SchedulingTests(fixture);
        Assert.True((await schedule.Extra(s.Doctor, s.Session.LocationId, schedule.Tomorrow, 18, 20)).IsError);
        Assert.True((await schedule.Call(s.Doctor, x => x.PublishAsync(s.Session.LocationId, schedule.Tomorrow, schedule.Tomorrow, false, default))).IsError);
        Assert.True((await schedule.Edit(s.Doctor, s.Session, SessionEditKind.Resume)).IsError);
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc;
        Assert.Equal(AppointmentStatus.CheckedIn, (await CheckIn(s, 0)).Facts.Status);
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(30);
        var current = await attendance.Current(s.Doctor, s, s.Rows[0].Id);
        Assert.True((await attendance.Write(s.Receptionist, s, current, AttendanceAction.Complete, completed: s.Session.StartsAtUtc.AddMinutes(10))).IsSuccess);
        Assert.True((await Get(s.Doctor, pending.Id)).Value.CanFinalize);
    }

    [SqlServerFact]
    public async Task FutureConfirmedAndPastCheckedInVisitsBothBlockFinalizationAndCannotUseRoutineSessionCancellation()
    {
        var s = await Setup(); fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(-10);
        var future = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        Assert.Equal(LifecycleErrors.Outstanding, Assert.Single((await Change(s.Doctor, future, true)).Errors));
        var cancel = await new AppointmentTests(fixture).Call(s.Doctor, x => x.CancelSessionAsync(s.Session.Id, s.Session.Version, "Attempted shortcut", default));
        Assert.Equal(LifecycleErrors.EmergencyRequired, Assert.Single(cancel.Errors));
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc; await CheckIn(s, 0); fixture.Database.Clock.UtcNow = s.Session.EndsAtUtc.AddDays(5);
        var pending = (await Get(s.Doctor, s.Session.LocationId)).Value;
        Assert.Equal(1, pending.CheckedInCount); Assert.Equal(LifecycleErrors.Outstanding, Assert.Single((await Change(s.Doctor, pending, true)).Errors));
        var row = await attendance.Current(s.Doctor, s, s.Rows[0].Id);
        Assert.True((await attendance.Write(s.Doctor, s, row, AttendanceAction.NoShow)).IsError);
    }

    [SqlServerFact]
    public async Task NormalDepartureKeepsCompletedCapacityQueueAndHistoryWhileOtherLocationsRemainUsable()
    {
        var s = await Setup(); var completed = await Complete(s, 0);
        var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var ended = (await Change(s.Doctor, pending, true)).Value; Assert.Equal("Ended", ended.State); Assert.False(ended.IsActive);
        Assert.Equal(ended, (await Change(s.Doctor, pending, true)).Value);
        await using var db = fixture.Database.CreateContext(); var stored = await db.BookingSessions.SingleAsync(x => x.Id == s.Session.Id);
        Assert.Equal(1, stored.AllocatedCount); Assert.Equal(1, stored.LastQueueNumber); Assert.Equal("Cancelled", stored.Status.ToString());
        Assert.Equal(AppointmentStatus.Completed, (await db.Appointments.SingleAsync(a => a.Id == s.Rows[0].Id)).Status);
        Assert.Equal(2, await db.OperationalScopeRecords.CountAsync(r => r.LocationId == ended.Id));
        Assert.True((await attendance.Write(s.Doctor, s, completed, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Cannot reopen ended scope")).IsError);
        var schedule = new SchedulingTests(fixture); var other = await schedule.SecondPlace(s.Doctor);
        Assert.True((await schedule.Extra(s.Doctor, other.Id, schedule.Tomorrow, 18, 20)).IsSuccess);
    }

    [SqlServerFact]
    public async Task OwnerAndManagerScopeChecksRejectOtherActorsWrongClientsAndRevokedRoles()
    {
        var s = await Setup(); var otherDoctor = (await new SchedulingTests(fixture).Place()).Doctor;
        Assert.Equal(LifecycleErrors.NotFound, Assert.Single((await Get(otherDoctor, s.Session.LocationId)).Errors));
        Assert.Equal(LifecycleErrors.Forbidden, Assert.Single((await Get(s.Receptionist, s.Session.LocationId)).Errors));
        Assert.Equal(LifecycleErrors.Forbidden, Assert.Single((await Get(s.Patients[0], s.Session.LocationId)).Errors));
        Assert.Equal(LifecycleErrors.Unauthorized, Assert.Single((await Call(null, x => x.GetAsync(OperationalScopeKind.Location, s.Session.LocationId, default))).Errors));
        Assert.Equal(LifecycleErrors.Forbidden, Assert.Single((await Call(s.Doctor, x => x.GetAsync(OperationalScopeKind.Location, s.Session.LocationId, default), "Mobile")).Errors));
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AspNetUserRoles WHERE UserId={s.Doctor}");
        Assert.Equal(LifecycleErrors.Forbidden, Assert.Single((await Get(s.Doctor, s.Session.LocationId)).Errors));
    }

    [SqlServerFact]
    public async Task EmergencyApprovalCancelsOnlyOutstandingVisitsPreservingArrivalCompletedAndNotSeenFacts()
    {
        var s = await Setup(4); await Complete(s, 2);
        var waiting = await CheckIn(s, 1); var left = await CheckIn(s, 3);
        Assert.True((await attendance.Write(s.Doctor, s, left, AttendanceAction.NotSeen, reason: "Left without consultation")).IsSuccess);
        var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; var detail = (await Detail(request.Id)).Value;
        Assert.Equal(2, detail.AffectedVisits.TotalCount);
        var json = JsonSerializer.Serialize(detail.AffectedVisits);
        foreach (var secret in new[] { "PatientId", "PatientUserId", "Contact", "Email", "Phone" }) Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        var approved = (await Decide(detail)).Value; Assert.Equal(EmergencyRequestStatus.Approved, approved.Status); Assert.Equal(2, approved.CancelledCount);
        Assert.Equal(approved, (await Decide(detail)).Value);
        await using var db = fixture.Database.CreateContext(); var rows = await db.Appointments.Where(a => a.SessionId == s.Session.Id).ToDictionaryAsync(a => a.Id);
        Assert.Equal(AppointmentStatus.Cancelled, rows[s.Rows[0].Id].Status); Assert.Equal(AppointmentStatus.Cancelled, rows[s.Rows[1].Id].Status);
        Assert.Equal(waiting.Facts.ArrivedAtUtc, rows[s.Rows[1].Id].ArrivedAtUtc); Assert.Equal(waiting.Facts.QueueNumber, rows[s.Rows[1].Id].QueueNumber);
        Assert.Equal(AppointmentStatus.CheckedIn, rows[s.Rows[1].Id].StatusBeforeCancellation);
        Assert.Equal(AppointmentStatus.Completed, rows[s.Rows[2].Id].Status); Assert.Equal(AppointmentStatus.AttendedNotSeen, rows[s.Rows[3].Id].Status);
        var session = await db.BookingSessions.SingleAsync(x => x.Id == s.Session.Id); Assert.Equal(2, session.AllocatedCount); Assert.Equal(3, session.LastQueueNumber);
        Assert.Equal(2, await db.EmergencyCancellationRecords.CountAsync(r => r.RequestId == request.Id));
        Assert.Equal(4, await db.BookingNotices.CountAsync(n => (n.AppointmentId == s.Rows[0].Id || n.AppointmentId == s.Rows[1].Id) && n.Kind == BookingNoticeKind.Cancelled));
        Assert.Equal("Ended", (await Change(s.Doctor, (await Get(s.Doctor, pending.Id)).Value, true)).Value.State);
    }

    [SqlServerFact]
    public async Task BranchPendingRetainsReceptionAndFinalClosureDoesNotDisableDoctorElsewhere()
    {
        var s = await Setup(managed: true); var owner = s.Manager!.Value;
        var pending = (await Change(owner, (await Get(owner, s.BranchId!.Value, true)).Value)).Value;
        Assert.Equal("PendingClosure", pending.State); Assert.False((await Get(s.Doctor, s.Session.LocationId)).Value.AcceptingNewBookings);
        Assert.True((await attendance.Call(s.Receptionist, x => x.QueueAsync(s.Session.DoctorProfileId, s.Session.Id, 1, 20, default))).IsSuccess);
        var request = (await Submit(owner, pending)).Value; Assert.True((await Decide((await Detail(request.Id)).Value)).IsSuccess);
        Assert.Equal("Closed", (await Change(owner, (await Get(owner, pending.Id, true)).Value, true)).Value.State);
        Assert.True((await Get(owner, pending.Id, true)).IsSuccess);
        Assert.True((await attendance.Call(s.Receptionist, x => x.QueueAsync(s.Session.DoctorProfileId, s.Session.Id, 1, 20, default))).IsError);
        await using var db = fixture.Database.CreateContext();
        var independent = await db.DoctorPracticeLocations.SingleAsync(p => p.UserId == s.Doctor && p.ClinicBranchId == null);
        var schedule = new SchedulingTests(fixture); Assert.True((await schedule.Extra(s.Doctor, independent.Id, schedule.Tomorrow, 18, 20)).IsSuccess);
    }

    [SqlServerFact]
    public async Task StalePreviewCannotCancelVisitsAfterAttendanceChangesAndRejectionNeverCancels()
    {
        var s = await Setup(); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; var old = (await Detail(request.Id)).Value;
        await CheckIn(s, 0); Assert.Equal(LifecycleErrors.Stale, Assert.Single((await Decide(old)).Errors));
        Assert.Equal(EmergencyRequestStatus.Rejected, (await Decide((await Detail(request.Id)).Value, false)).Value.Status);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(AppointmentStatus.CheckedIn, (await db.Appointments.SingleAsync(a => a.Id == s.Rows[0].Id)).Status);
        Assert.False(await db.EmergencyCancellationRecords.AnyAsync(r => r.RequestId == request.Id));
        Assert.Equal("PendingDeparture", (await Get(s.Doctor, pending.Id)).Value.State);
        Assert.NotEqual(request.Id, (await Submit(s.Doctor, (await Get(s.Doctor, pending.Id)).Value)).Value.Id);
    }

    [SqlServerFact]
    public async Task ConcurrentEmergencyDecisionsHaveOneWinnerAndOneCancellationPerBooking()
    {
        var s = await Setup(); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; var detail = (await Detail(request.Id)).Value;
        var results = await Task.WhenAll(Decide(detail), Decide(detail, false)); Assert.Single(results, r => r.IsSuccess); Assert.Single(results, r => r.IsError);
        await using var db = fixture.Database.CreateContext(); var final = await db.OperationalEmergencies.SingleAsync(r => r.Id == request.Id);
        Assert.Equal(final.Status == EmergencyRequestStatus.Approved ? 1 : 0, await db.EmergencyCancellationRecords.CountAsync(r => r.RequestId == request.Id));
    }

    [SqlServerFact]
    public async Task BookingAndPendingDepartureSerializeWithoutLosingOutstandingCommitments()
    {
        var schedule = new SchedulingTests(fixture); var (doctor, place) = await schedule.Place(); var session = (await schedule.Extra(doctor, place.Id, schedule.Tomorrow, 18, 20)).Value;
        var helper = new AppointmentTests(fixture); var patient = await helper.Patient(); var input = AppointmentTests.Accept((await helper.Preview(patient, session.Id)).Value);
        var scope = (await Get(doctor, place.Id)).Value; var bookTask = helper.Call(patient, x => x.BookAsync(input, default)); var beginTask = Change(doctor, scope);
        await Task.WhenAll(bookTask, beginTask); Assert.True(beginTask.Result.IsSuccess);
        var pending = (await Get(doctor, place.Id)).Value;
        Assert.Equal(bookTask.Result.IsSuccess ? 1 : 0, pending.ConfirmedCount);
        if (bookTask.Result.IsSuccess) Assert.Equal(LifecycleErrors.Outstanding, Assert.Single((await Change(doctor, pending, true)).Errors));
    }

    [SqlServerFact]
    public async Task CompletionCorrectionAndFinalizationCannotReopenAClosedScope()
    {
        var s = await Setup(); var completed = await Complete(s, 0); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var end = Change(s.Doctor, pending, true);
        var correction = attendance.Write(s.Doctor, s, completed, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Synthetic completion correction");
        await Task.WhenAll(end, correction); await using var db = fixture.Database.CreateContext();
        var place = await db.DoctorPracticeLocations.SingleAsync(p => p.Id == pending.Id); var a = await db.Appointments.SingleAsync(a => a.Id == completed.Id);
        if (place.LifecycleState == PracticeLifecycleState.Ended) { Assert.True(end.Result.IsSuccess); Assert.True(correction.Result.IsError); Assert.Equal(AppointmentStatus.Completed, a.Status); }
        else { Assert.True(end.Result.IsError); Assert.True(correction.Result.IsSuccess); Assert.Equal(AppointmentStatus.CheckedIn, a.Status); }
    }

    [SqlServerFact]
    public async Task EmergencyApprovalAndCompletionRacePreservesExactlyOneOutcome()
    {
        var s = await Setup(); var waiting = await CheckIn(s, 0); fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(30);
        var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; var d = (await Detail(request.Id)).Value;
        var approval = Decide(d); var completion = attendance.Write(s.Doctor, s, waiting, AttendanceAction.Complete, completed: s.Session.StartsAtUtc.AddMinutes(10));
        await Task.WhenAll(approval, completion); await using var db = fixture.Database.CreateContext(); var a = await db.Appointments.SingleAsync(a => a.Id == waiting.Id);
        Assert.True(a.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled);
        Assert.Equal(a.Status == AppointmentStatus.Cancelled, approval.Result.IsSuccess); Assert.Equal(a.Status == AppointmentStatus.Completed, completion.Result.IsSuccess);
    }

    [SqlServerFact]
    public async Task AuditFailureRollsBackPendingTransitionAndEmergencyDecisionAtomically()
    {
        var s = await Setup(); var scope = (await Get(s.Doctor, s.Session.LocationId)).Value; await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_Test_LifecycleFailure ON OperationalScopeRecords AFTER INSERT AS THROW 51998, 'Synthetic history failure', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Change(s.Doctor, scope)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_Test_LifecycleFailure;"); }
        Assert.Equal("Active", (await Get(s.Doctor, scope.Id)).Value.State);
        var pending = (await Change(s.Doctor, scope)).Value; var request = (await Submit(s.Doctor, pending)).Value;
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_Test_EmergencyFailure ON EmergencyCancellationRecords AFTER INSERT AS THROW 51998, 'Synthetic cancellation audit failure', 1;");
        var detail = (await Detail(request.Id)).Value;
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Decide(detail)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_Test_EmergencyFailure;"); }
        Assert.Equal(EmergencyRequestStatus.Pending, (await db.OperationalEmergencies.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);
        Assert.Equal(AppointmentStatus.Confirmed, (await db.Appointments.SingleAsync(a => a.Id == s.Rows[0].Id)).Status);
        Assert.False(await db.BookingNotices.AnyAsync(n => n.AppointmentId == s.Rows[0].Id && n.Kind == BookingNoticeKind.Cancelled));
    }

    [SqlServerFact]
    public async Task ImmutableEvidenceConstraintsAndRollbackGuardProtectOperationalHistory()
    {
        var s = await Setup(); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; await Decide((await Detail(request.Id)).Value);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(51018, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OperationalScopeRecords SET Reason='Rewrite' WHERE LocationId={pending.Id}"))).Number);
        Assert.Equal(51019, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OperationalEmergencies SET Reason='Rewrite' WHERE Id={request.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM OperationalEmergencies WHERE Id={request.Id}"))).Number);
        Assert.Equal(51019, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OperationalEmergencies SET ReviewReason='Overwrite decision' WHERE Id={request.Id}"))).Number);
        Assert.Equal(51020, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM EmergencyCancellationRecords WHERE RequestId={request.Id}"))).Number);
        var migrator = db.GetService<IMigrator>();
        try { Assert.Equal(51021, (await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261030153407_VisitReviews"))).Number); }
        finally { await migrator.MigrateAsync(); }
        var ended = (await Change(s.Doctor, (await Get(s.Doctor, pending.Id)).Value, true)).Value;
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET IsActive=1 WHERE Id={ended.Id}"))).Number);
    }

    [SqlServerFact]
    public async Task UnavailableDeliveryDoesNotReverseApprovalAndExistingRetryDoesNotDuplicateCancellation()
    {
        var s = await Setup(); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value; var environment = fixture.Services.GetRequiredService<IHostEnvironment>(); var original = environment.EnvironmentName;
        try { environment.EnvironmentName = Environments.Production; Assert.True((await Decide((await Detail(request.Id)).Value)).IsSuccess); }
        finally { environment.EnvironmentName = original; }
        await using var db = fixture.Database.CreateContext();
        var notices = await db.BookingNotices.Where(n => n.AppointmentId == s.Rows[0].Id && n.Kind == BookingNoticeKind.Cancelled).ToArrayAsync();
        Assert.Equal(2, notices.Length); Assert.All(notices, n => Assert.Equal("Unavailable", n.DeliveryStatus));
        fixture.Database.Clock.UtcNow = fixture.Database.Clock.GetUtcNow().AddMinutes(2);
        await new AppointmentTests(fixture).Call(fixture.Admin.Id, x => x.RetryNoticesAsync(default));
        Assert.All(await db.BookingNotices.AsNoTracking().Where(n => n.AppointmentId == s.Rows[0].Id && n.Kind == BookingNoticeKind.Cancelled).ToArrayAsync(), n => Assert.Equal("DevelopmentFile", n.DeliveryStatus));
        Assert.Equal(1, await db.EmergencyCancellationRecords.CountAsync(r => r.RequestId == request.Id));
    }

    [SqlServerFact]
    public async Task BranchClosureCoversAllAffiliatedDoctorsAndRejectsAPreviouslyIssuedDoctorInvitation()
    {
        var first = await Setup(managed: true); var owner = first.Manager!.Value; var branch = first.BranchId!.Value;
        var schedule = new SchedulingTests(fixture); var (secondDoctor, _) = await schedule.Place();
        var (lateDoctor, _) = await schedule.Place(); var invitations = new WorkInvitationTests(fixture);
        await using var db = fixture.Database.CreateContext();
        var email = (await db.Users.SingleAsync(u => u.Id == secondDoctor)).Email!;
        var invitation = (await invitations.Issue(owner, ClinIQ.Domain.Invitations.WorkInvitationKind.Doctor, branch, null, email, 100)).Value;
        Assert.True((await invitations.Respond(secondDoctor, invitation)).IsSuccess);
        var secondPlace = await db.DoctorPracticeLocations.SingleAsync(p => p.UserId == secondDoctor && p.ClinicBranchId == branch);
        var secondSession = (await schedule.Extra(secondDoctor, secondPlace.Id, schedule.Tomorrow, 18, 20)).Value;
        var helper = new AppointmentTests(fixture); var secondPatient = await helper.Patient(); Assert.True((await helper.Book(secondPatient, secondSession.Id)).IsSuccess);
        var lateEmail = (await db.Users.SingleAsync(u => u.Id == lateDoctor)).Email!;
        var oldInvitation = (await invitations.Issue(owner, ClinIQ.Domain.Invitations.WorkInvitationKind.Doctor, branch, null, lateEmail, 100)).Value;
        var pending = (await Change(owner, (await Get(owner, branch, true)).Value)).Value;
        Assert.Equal(2, pending.ConfirmedCount); Assert.True((await invitations.Respond(lateDoctor, oldInvitation)).IsError);
        var request = (await Submit(owner, pending)).Value; var detail = (await Detail(request.Id)).Value;
        Assert.Equal(2, detail.AffectedVisits.Items.Select(a => a.DoctorProfileId).Distinct().Count());
        Assert.Equal(2, (await Decide(detail)).Value.CancelledCount);
        Assert.Equal(2, (await Call(fixture.Admin.Id, x => x.CancellationsAsync(request.Id, 1, 100, default))).Value.TotalCount);
        Assert.Equal("Closed", (await Change(owner, (await Get(owner, branch, true)).Value, true)).Value.State);
        Assert.Equal(0, await db.BookingSessions.Where(s => s.OperationalBranchId == branch).SumAsync(s => s.AllocatedCount));
    }

    [SqlServerFact]
    public async Task LegitimateResolutionAllowsNormalFinalizationAndLaterZeroActionEmergencyReview()
    {
        var s = await Setup(); var pending = (await Change(s.Doctor, (await Get(s.Doctor, s.Session.LocationId)).Value)).Value;
        var request = (await Submit(s.Doctor, pending)).Value;
        fixture.Database.Clock.UtcNow = s.Session.EndsAtUtc;
        Assert.True((await attendance.Write(s.Doctor, s, s.Rows[0], AttendanceAction.NoShow)).IsSuccess);
        Assert.Equal("Ended", (await Change(s.Doctor, (await Get(s.Doctor, pending.Id)).Value, true)).Value.State);
        var detail = (await Detail(request.Id)).Value; Assert.Empty(detail.AffectedVisits.Items);
        var approved = (await Decide(detail)).Value; Assert.Equal(0, approved.CancelledCount);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(AppointmentStatus.NoShow, (await db.Appointments.SingleAsync(a => a.Id == s.Rows[0].Id)).Status);
        Assert.False(await db.EmergencyCancellationRecords.AnyAsync(r => r.RequestId == request.Id));
        Assert.False(await db.BookingNotices.AnyAsync(n => n.AppointmentId == s.Rows[0].Id && n.Kind == BookingNoticeKind.Cancelled));
    }
}
