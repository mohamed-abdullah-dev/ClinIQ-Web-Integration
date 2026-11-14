using System.Security.Claims;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AttendanceFeedbackTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private readonly AttendanceTests attendance = new(fixture);
    private Task<T> Call<T>(Guid? actor, bool admin, Func<IAttendanceFeedbackService, Task<T>> action, string? overrideClient = null) => fixture.AsAsync(actor, p =>
    {
        var principal = p.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        (principal?.Identity as ClaimsIdentity)?.AddClaim(new("client", overrideClient ?? (admin ? "Web" : "Mobile")));
        return action(p.GetRequiredService<IAttendanceFeedbackService>());
    });
    private async Task<(AttendanceTests.Scenario Scenario, FeedbackDto Feedback)> Setup()
    {
        var s = await attendance.Setup(); fixture.Database.Clock.UtcNow = s.Session.EndsAtUtc;
        Assert.True((await attendance.Write(s.Receptionist, s, s.Rows[0], AttendanceAction.NoShow)).IsSuccess);
        var page = (await Call(s.Patients[0], false, x => x.ListAsync(1, 100, default))).Value;
        return (s, Assert.Single(page.Items, x => x.AppointmentId == s.Rows[0].Id));
    }
    private Task<Result<FeedbackAnswerReceipt>> Answer(Guid patient, FeedbackDto r, PatientAttendanceOutcome outcome = PatientAttendanceOutcome.AttendedAndSeen,
        bool edit = false, Guid? submission = null) => Call(patient, false, s => s.AnswerAsync(r.Id, new(submission ?? Guid.NewGuid(), outcome, r.Version), edit, default));
    private Task<Result<FeedbackReviewDetail>> Review(Guid id) => Call(fixture.Admin.Id, true, s => s.ReviewAsync(id, default));
    private Task<Result<FeedbackReviewDetail>> Close(FeedbackReviewDetail r, AttendanceReviewState state = AttendanceReviewState.Inconclusive,
        PatientAttendanceOutcome? finding = null, DateTimeOffset? arrival = null, DateTimeOffset? complete = null) => Call(fixture.Admin.Id, true,
        s => s.FinalizeAsync(r.Feedback.Id, new(r.Feedback.Version, r.AppointmentVersion, state, finding, "Synthetic clarification recorded", arrival, complete), default));

    [SqlServerFact]
    public async Task ValidNoShowAtomicallyCreatesOneEventBoundRequestAndReplaysDoNotResetItsDeadline()
    {
        var (s, r) = await Setup(); fixture.Database.Clock.UtcNow = r.CreatedAtUtc.AddDays(2);
        Assert.True((await attendance.Write(s.Receptionist, s, s.Rows[0], AttendanceAction.NoShow)).IsSuccess);
        await using var db = fixture.Database.CreateContext(); var request = Assert.Single(await db.AttendanceFeedbackRequests.Where(x => x.AppointmentId == r.AppointmentId).ToArrayAsync());
        var source = await db.AttendanceRecords.SingleAsync(x => x.Id == request.SourceAttendanceRecordId);
        Assert.Equal(r.CreatedAtUtc, request.CreatedAtUtc); Assert.Equal(r.CreatedAtUtc.AddDays(7), request.ExpiresAtUtc);
        Assert.Equal(request.SourceRevision, source.Revision); Assert.Equal(s.Patients[0], request.PatientUserId); Assert.Null(request.Outcome); Assert.Null(request.ReviewState);
        Assert.Equal(AppointmentStatus.NoShow, (await db.Appointments.SingleAsync(x => x.Id == r.AppointmentId)).Status);
    }
    [SqlServerFact]
    public async Task FeedbackInsertFailureRollsBackTheAttendanceAndItsAudit()
    {
        var s = await attendance.Setup(); fixture.Database.Clock.UtcNow = s.Session.EndsAtUtc;
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_Test_FeedbackFailure ON AttendanceFeedbackRequests AFTER INSERT AS THROW 51013, 'Synthetic insert failure', 1;");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => attendance.Write(s.Receptionist, s, s.Rows[0], AttendanceAction.NoShow));
            Assert.Equal(AppointmentStatus.Confirmed, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == s.Rows[0].Id)).Status);
            Assert.False(await db.AttendanceRecords.AnyAsync(a => a.AppointmentId == s.Rows[0].Id)); Assert.False(await db.AttendanceFeedbackRequests.AnyAsync(a => a.AppointmentId == s.Rows[0].Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_Test_FeedbackFailure;"); }
    }
    [SqlServerFact]
    public async Task OwnerAndMobileChecksApplyToReadsAndWritesAndSilenceIsNeverAnAnswer()
    {
        var (s, r) = await Setup(); var other = await new AppointmentTests(fixture).Patient();
        Assert.Equal(FeedbackErrors.NotFound, Assert.Single((await Call(other, false, x => x.GetAsync(r.Id, default))).Errors));
        Assert.Equal(FeedbackErrors.NotFound, Assert.Single((await Answer(other, r)).Errors));
        Assert.Equal(FeedbackErrors.Forbidden, Assert.Single((await Call(s.Patients[0], false, x => x.GetAsync(r.Id, default), "Web")).Errors));
        Assert.Equal(FeedbackErrors.Forbidden, Assert.Single((await Call(s.Doctor, false, x => x.GetAsync(r.Id, default))).Errors));
        Assert.Equal(FeedbackErrors.Unauthorized, Assert.Single((await Call(null, false, x => x.GetAsync(r.Id, default))).Errors));
        Assert.Null((await Call(s.Patients[0], false, x => x.GetAsync(r.Id, default))).Value.Outcome);
        Assert.Equal(FeedbackErrors.NotFound, Assert.Single((await Review(r.Id)).Errors));
    }
    [SqlServerTheory]
    [InlineData(PatientAttendanceOutcome.AttendedAndSeen, AttendanceReviewState.Open)]
    [InlineData(PatientAttendanceOutcome.AttendedNotSeen, AttendanceReviewState.Open)]
    [InlineData(PatientAttendanceOutcome.DidNotAttend, null)]
    public async Task OutcomesNeverAutomaticallyChangeAttendance(PatientAttendanceOutcome outcome, AttendanceReviewState? review)
    {
        var (s, r) = await Setup(); var result = (await Answer(s.Patients[0], r, outcome)).Value;
        Assert.Equal(review, result.Current.ReviewState); Assert.Equal(outcome, result.Current.Outcome);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(AppointmentStatus.NoShow, (await db.Appointments.SingleAsync(a => a.Id == r.AppointmentId)).Status);
        Assert.Equal(1, await db.AttendanceFeedbackResponses.CountAsync(a => a.FeedbackRequestId == r.Id));
    }
    [SqlServerFact]
    public async Task ConcurrentIdenticalSubmissionsHaveOneReceiptAndRetryRemainsSafeAfterExpiry()
    {
        var (s, r) = await Setup(); var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Answer(s.Patients[0], r, submission: id)));
        Assert.All(results, x => Assert.True(x.IsSuccess)); Assert.Single(results, x => !x.Value.Replayed);
        Assert.Single(results.Select(x => x.Value.Submitted.Id).Distinct());
        fixture.Database.Clock.UtcNow = r.ExpiresAtUtc.AddDays(1);
        Assert.True((await Answer(s.Patients[0], r, submission: id)).Value.Replayed);
        Assert.Equal(FeedbackErrors.ReplayMismatch, Assert.Single((await Answer(s.Patients[0], r, PatientAttendanceOutcome.DidNotAttend, submission: id)).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.AttendanceFeedbackResponses.CountAsync(x => x.FeedbackRequestId == r.Id));
    }
    [SqlServerFact]
    public async Task ConcurrentDifferentAnswersUseVersionChecksAndCannotDuplicateTheCase()
    {
        var (s, r) = await Setup(); var results = await Task.WhenAll(Answer(s.Patients[0], r), Answer(s.Patients[0], r, PatientAttendanceOutcome.AttendedNotSeen));
        Assert.Single(results, x => x.IsSuccess); Assert.Equal(FeedbackErrors.Stale, Assert.Single(Assert.Single(results, x => x.IsError).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.AttendanceFeedbackRequests.CountAsync(x => x.AppointmentId == r.AppointmentId));
        Assert.Equal(1, await db.AttendanceFeedbackResponses.CountAsync(x => x.FeedbackRequestId == r.Id));
    }
    [SqlServerFact]
    public async Task ExplicitEditsKeepAnswerHistoryDeadlineAndAnAlreadyOpenedCase()
    {
        var (s, r) = await Setup(); var first = (await Answer(s.Patients[0], r)).Value;
        Assert.Equal(FeedbackErrors.ExplicitEdit, Assert.Single((await Answer(s.Patients[0], first.Current, PatientAttendanceOutcome.DidNotAttend)).Errors));
        var edited = (await Answer(s.Patients[0], first.Current, PatientAttendanceOutcome.DidNotAttend, true)).Value;
        Assert.Equal(AttendanceReviewState.Open, edited.Current.ReviewState); Assert.Equal(r.ExpiresAtUtc, edited.Current.ExpiresAtUtc);
        var history = (await Call(s.Patients[0], false, x => x.ResponsesAsync(r.Id, 1, 20, false, default))).Value.Items;
        Assert.Equal(new[] { 1, 2 }, history.Select(x => x.Revision)); Assert.All(history, x => Assert.Equal(s.Patients[0], x.ActorUserId));
        Assert.Equal(PatientAttendanceOutcome.AttendedAndSeen, history[0].Outcome); Assert.Equal(PatientAttendanceOutcome.DidNotAttend, history[1].Outcome);
        Assert.Equal(2, (await Review(r.Id)).Value.Feedback.ResponseRevision);
    }
    [SqlServerFact]
    public async Task InitialAndEditedAnswersBothRejectTheExactDeadline()
    {
        var (s, r) = await Setup(); fixture.Database.Clock.UtcNow = r.ExpiresAtUtc;
        Assert.Equal(FeedbackErrors.Expired, Assert.Single((await Answer(s.Patients[0], r)).Errors));
        fixture.Database.Clock.UtcNow = r.CreatedAtUtc;
        var first = (await Answer(s.Patients[0], r)).Value.Current;
        fixture.Database.Clock.UtcNow = r.ExpiresAtUtc;
        Assert.Equal(FeedbackErrors.Expired, Assert.Single((await Answer(s.Patients[0], first, PatientAttendanceOutcome.DidNotAttend, true)).Errors));
    }
    [SqlServerFact]
    public async Task StaffCorrectionPreservesTheCaseAndInvalidatesStalePatientActionsAndAdminViews()
    {
        var (s, r) = await Setup(); var first = (await Answer(s.Patients[0], r)).Value.Current; var review = (await Review(r.Id)).Value;
        var current = await attendance.Current(s.Doctor, s, r.AppointmentId);
        Assert.True((await attendance.Write(s.Receptionist, s, current, AttendanceAction.Correct, arrived: s.Session.StartsAtUtc.AddMinutes(5), target: AppointmentStatus.AttendedNotSeen, reason: "Missed check-in corrected")).IsSuccess);
        Assert.Equal(FeedbackErrors.Inapplicable, Assert.Single((await Answer(s.Patients[0], first, PatientAttendanceOutcome.AttendedNotSeen, true)).Errors));
        Assert.Equal(FeedbackErrors.Stale, Assert.Single((await Close(review)).Errors));
        var live = (await Review(r.Id)).Value; Assert.Equal(AttendanceReviewState.Open, live.Feedback.ReviewState); Assert.False(live.Feedback.Applicable);
        Assert.True((await Close(live)).IsSuccess);
        var history = (await Call(fixture.Admin.Id, true, x => x.StaffHistoryAsync(r.Id, 1, 20, default))).Value.Items;
        Assert.Equal(new[] { "NoShow", "Correct" }, history.Select(x => x.Action));
    }
    [SqlServerTheory]
    [InlineData(PatientAttendanceOutcome.AttendedAndSeen, AppointmentStatus.Completed)]
    [InlineData(PatientAttendanceOutcome.AttendedNotSeen, AppointmentStatus.AttendedNotSeen)]
    [InlineData(PatientAttendanceOutcome.DidNotAttend, AppointmentStatus.NoShow)]
    public async Task AdminResolutionRequiresRealFactsAndCommitsTheCorrectionWithTheDecision(PatientAttendanceOutcome finding, AppointmentStatus expected)
    {
        var (s, r) = await Setup(); await Answer(s.Patients[0], r); var review = (await Review(r.Id)).Value;
        var arrival = finding == PatientAttendanceOutcome.DidNotAttend ? (DateTimeOffset?)null : s.Session.StartsAtUtc.AddMinutes(5);
        var completed = finding == PatientAttendanceOutcome.AttendedAndSeen ? s.Session.StartsAtUtc.AddMinutes(15) : (DateTimeOffset?)null;
        var closed = (await Close(review, AttendanceReviewState.Resolved, finding, arrival, completed)).Value;
        Assert.Equal(expected, closed.Attendance.Status); Assert.Equal(AttendanceReviewState.Resolved, closed.Feedback.ReviewState);
        Assert.Equal(review.AppointmentVersion, closed.ReviewedAppointmentVersion); Assert.Equal(fixture.Admin.Id, closed.ReviewedByUserId); Assert.Equal(1, closed.ReviewedResponseRevision);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, (await db.BookingSessions.SingleAsync(x => x.Id == s.Session.Id)).AllocatedCount);
        var source = await db.AttendanceRecords.SingleAsync(x => x.Id == r.SourceAttendanceRecordId); Assert.Equal(AppointmentStatus.NoShow, source.NewStatus);
        var rows = await db.AttendanceRecords.Where(x => x.AppointmentId == r.AppointmentId).OrderBy(x => x.Revision).ToArrayAsync();
        Assert.Equal(finding == PatientAttendanceOutcome.DidNotAttend ? 1 : 2, rows.Length);
        if (rows.Length == 2) Assert.Equal(fixture.Admin.Id, rows[1].ActorUserId);
        Assert.Equal(FeedbackErrors.Final, Assert.Single((await Answer(s.Patients[0], closed.Feedback, PatientAttendanceOutcome.DidNotAttend, true)).Errors));
    }
    [SqlServerFact]
    public async Task AdminCannotInventArrivalOrBypassDoctorOnlyLateAuthorization()
    {
        var (s, r) = await Setup(); await Answer(s.Patients[0], r); var review = (await Review(r.Id)).Value;
        Assert.Equal(AttendanceErrors.Facts, Assert.Single((await Close(review, AttendanceReviewState.Resolved, PatientAttendanceOutcome.AttendedAndSeen, complete: s.Session.StartsAtUtc.AddMinutes(20))).Errors));
        Assert.Equal(AttendanceErrors.Late, Assert.Single((await Close(review, AttendanceReviewState.Resolved, PatientAttendanceOutcome.AttendedNotSeen, arrival: s.Session.EndsAtUtc)).Errors));
        Assert.Equal(AttendanceReviewState.Open, (await Review(r.Id)).Value.Feedback.ReviewState);
    }
    [SqlServerFact]
    public async Task ConcurrentPatientEditAndAdminDecisionNeverOverwriteEachOther()
    {
        var (s, r) = await Setup(); var current = (await Answer(s.Patients[0], r)).Value.Current; var review = (await Review(r.Id)).Value;
        var editTask = Answer(s.Patients[0], current, PatientAttendanceOutcome.AttendedNotSeen, true); var finalizeTask = Close(review);
        await Task.WhenAll(editTask, finalizeTask);
        Assert.NotEqual(editTask.Result.IsSuccess, finalizeTask.Result.IsSuccess);
        var failure = editTask.Result.IsError ? editTask.Result.Errors : finalizeTask.Result.Errors;
        Assert.Equal(FeedbackErrors.Stale, Assert.Single(failure));
        var live = (await Review(r.Id)).Value;
        if (editTask.Result.IsSuccess) Assert.True((await Close(live)).IsSuccess);
        var final = (await Review(r.Id)).Value; Assert.Equal(AttendanceReviewState.Inconclusive, final.Feedback.ReviewState);
        Assert.Equal(final.Feedback.ResponseRevision, final.ReviewedResponseRevision);
    }
    [SqlServerFact]
    public async Task ClosedScopeAllowsInconclusiveClosureButDoesNotPermitAttendanceResurrection()
    {
        var (s, r) = await Setup(); await Answer(s.Patients[0], r);
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET IsActive=0 WHERE Id={s.Session.LocationId}");
        var review = (await Review(r.Id)).Value;
        Assert.Equal(FeedbackErrors.Closed, Assert.Single((await Close(review, AttendanceReviewState.Resolved, PatientAttendanceOutcome.AttendedNotSeen, s.Session.StartsAtUtc.AddMinutes(5))).Errors));
        Assert.True((await Close((await Review(r.Id)).Value)).IsSuccess);
        Assert.Equal(AppointmentStatus.NoShow, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == r.AppointmentId)).Status);
    }
    [SqlServerFact]
    public async Task ANewStaffNoShowEventCannotBeAnsweredThroughAnOldRequest()
    {
        var (s, old) = await Setup(); var current = await attendance.Current(s.Doctor, s, old.AppointmentId);
        var reset = (await attendance.Write(s.Doctor, s, current, AttendanceAction.Correct, target: AppointmentStatus.Confirmed, reason: "Verify the recorded absence")).Value.Appointment;
        fixture.Database.Clock.UtcNow = old.CreatedAtUtc.AddHours(1);
        Assert.True((await attendance.Write(s.Doctor, s, reset, AttendanceAction.NoShow)).IsSuccess);
        var items = (await Call(s.Patients[0], false, x => x.ListAsync(1, 100, default))).Value.Items.Where(x => x.AppointmentId == old.AppointmentId).ToArray();
        Assert.Equal(2, items.Length); var fresh = Assert.Single(items, x => x.Applicable);
        Assert.NotEqual(old.SourceAttendanceRecordId, fresh.SourceAttendanceRecordId);
        Assert.Equal(FeedbackErrors.Inapplicable, Assert.Single((await Answer(s.Patients[0], old)).Errors));
        Assert.True((await Answer(s.Patients[0], fresh)).IsSuccess);
    }
    [SqlServerFact]
    public async Task RevokedAdminRoleCannotUseAnAlreadyAuthenticatedPrincipalToFinalize()
    {
        var (s, r) = await Setup(); await Answer(s.Patients[0], r); var reviewer = await fixture.UserAsync("Admin"); var review = (await Review(r.Id)).Value;
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AspNetUserRoles WHERE UserId={reviewer.Id}");
        var result = await Call(reviewer.Id, true, x => x.FinalizeAsync(r.Id, new(review.Feedback.Version, review.AppointmentVersion,
            AttendanceReviewState.Inconclusive, null, "Role removed before finalization"), default));
        Assert.Equal(FeedbackErrors.Forbidden, Assert.Single(result.Errors)); Assert.Equal(AttendanceReviewState.Open, (await Review(r.Id)).Value.Feedback.ReviewState);
    }
    [SqlServerFact]
    public async Task DatabaseProtectsResponseHistoryRequestOriginFinalDecisionAndMigrationDown()
    {
        var (s, r) = await Setup(); await Answer(s.Patients[0], r);
        await using var db = fixture.Database.CreateContext();
        var responseError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AttendanceFeedbackResponses SET Outcome=3 WHERE FeedbackRequestId={r.Id}"));
        Assert.Equal(51012, responseError.Number);
        var originError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AttendanceFeedbackRequests SET CreatedAtUtc=DATEADD(day,-1,CreatedAtUtc), ExpiresAtUtc=DATEADD(day,-1,ExpiresAtUtc) WHERE Id={r.Id}"));
        Assert.Equal(51011, originError.Number);
        Assert.True((await Close((await Review(r.Id)).Value)).IsSuccess);
        var finalError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AttendanceFeedbackRequests SET ReviewReason=N'Replace' WHERE Id={r.Id}"));
        Assert.Equal(51011, finalError.Number);
        var migrator = db.GetService<IMigrator>();
        try { Assert.Equal(51015, (await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028200441_AppointmentAttendance"))).Number); }
        finally { await migrator.MigrateAsync(); }
        Assert.True(await db.AttendanceFeedbackRequests.AnyAsync(x => x.Id == r.Id));
    }
}
