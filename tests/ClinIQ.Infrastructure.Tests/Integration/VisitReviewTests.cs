using System.Security.Claims;
using System.Text.Json;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.VisitReviews;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class VisitReviewTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private readonly AttendanceTests attendance = new(fixture);
    internal Task<T> Call<T>(Guid? actor, bool admin, Func<IVisitReviewService, Task<T>> action, string? client = null) => fixture.AsAsync(actor, p =>
    {
        (p.GetRequiredService<IHttpContextAccessor>().HttpContext?.User.Identity as ClaimsIdentity)?.AddClaim(new("client", client ?? (admin ? "Web" : "Mobile")));
        return action(p.GetRequiredService<IVisitReviewService>());
    });
    internal async Task<AttendanceTests.Scenario> Completed(int count = 1)
    {
        var s = await attendance.Setup(count);
        // The queue sorts tied booking times by Id, which need not match patient creation order.
        await using var db = fixture.Database.CreateContext();
        var bookings = await db.Appointments.Where(a => a.SessionId == s.Session.Id).ToDictionaryAsync(a => a.PatientUserId, a => a.Id);
        s = s with { Rows = s.Patients.Select(patient => s.Rows.Single(row => row.Id == bookings[patient])).ToArray() };
        foreach (var row in s.Rows) Assert.True((await attendance.Write(s.Doctor, s, row, AttendanceAction.CheckIn)).IsSuccess);
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(30);
        foreach (var row in s.Rows)
            Assert.True((await attendance.Write(s.Doctor, s, await attendance.Current(s.Doctor, s, row.Id), AttendanceAction.Complete,
                completed: s.Session.StartsAtUtc.AddMinutes(10))).IsSuccess);
        return s;
    }
    private Task<ClinIQ.Domain.Common.Results.Result<VisitReviewWriteResult>> Write(AttendanceTests.Scenario s, int index = 0, int stars = 1, string? text = "Synthetic review", bool edit = false, string? version = null) =>
        Call(s.Patients[index], false, r => r.WriteAsync(s.Rows[index].Id, new(stars, text, version), edit, default));
    private Task<ClinIQ.Domain.Common.Results.Result<VisitReviewDto>> Moderate(VisitReviewDto r, bool hide = true) =>
        Call(fixture.Admin.Id, true, s => s.ModerateAsync(r.Id, new(hide, hide ? ReviewModerationCategory.PrivateInformation : null, "Synthetic privacy moderation", r.Version), default));
    private Task<PublicDoctorProfile> Profile(Guid id) => fixture.AsAsync(null, async p =>
        (await p.GetRequiredService<IPublicDirectoryService>().DoctorAsync(id, new(), default)).Value);

    [SqlServerFact]
    public async Task ConcurrentIdenticalCreatePublishesOneLowRatingWithOneAuditAndSafeReceiptReplay()
    {
        var s = await Completed(); var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Write(s)));
        Assert.All(replies, r => Assert.True(r.IsSuccess)); Assert.Single(replies, r => !r.Value.Replayed);
        var review = replies[0].Value.Review;
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.VisitReviews.CountAsync(r => r.AppointmentId == s.Rows[0].Id));
        Assert.Equal(1, await db.VisitReviewAudits.CountAsync(r => r.ReviewId == review.Id));
        var visible = (await Call(null, false, x => x.PublicAsync(s.Session.DoctorProfileId, 1, 20, default))).Value;
        Assert.Equal(review.Id, Assert.Single(visible.Items).Id); Assert.Equal(1, (await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddDays(40);
        Assert.True((await Write(s)).Value.Replayed); Assert.Equal(VisitReviewErrors.Exists, Assert.Single((await Write(s, stars: 5)).Errors));
        Assert.Equal(1, await db.VisitReviewAudits.CountAsync(r => r.ReviewId == review.Id));
    }

    [SqlServerFact]
    public async Task DifferentConcurrentCreatesCannotOverwriteOneAnother()
    {
        var s = await Completed(); var replies = await Task.WhenAll(Write(s, stars: 2), Write(s, stars: 4));
        Assert.Single(replies, r => r.IsSuccess); Assert.Equal(VisitReviewErrors.Exists, Assert.Single(Assert.Single(replies, r => r.IsError).Errors));
    }

    [SqlServerFact]
    public async Task OwnershipClientRoleAndLiveDatabaseRoleApplyToWritesReadsAndModeration()
    {
        var s = await Completed(); var r = (await Write(s)).Value.Review; var other = await new AppointmentTests(fixture).Patient();
        Assert.Equal(VisitReviewErrors.NotFound, Assert.Single((await Call(other, false, x => x.OwnAsync(r.AppointmentId, default))).Errors));
        Assert.Equal(VisitReviewErrors.NotFound, Assert.Single((await Call(other, false, x => x.WriteAsync(r.AppointmentId, new(5, null), false, default))).Errors));
        Assert.Equal(VisitReviewErrors.Unauthorized, Assert.Single((await Call(null, false, x => x.OwnAsync(r.AppointmentId, default))).Errors));
        Assert.Equal(VisitReviewErrors.Forbidden, Assert.Single((await Call(s.Patients[0], false, x => x.OwnAsync(r.AppointmentId, default), "Web")).Errors));
        foreach (var actor in new[] { s.Doctor, s.Receptionist, (await fixture.UserAsync("ClinicManager")).Id, s.Patients[0] })
            Assert.Equal(VisitReviewErrors.Forbidden, Assert.Single((await Call(actor, true, x => x.ModerateAsync(r.Id,
                new(true, ReviewModerationCategory.Abusive, "Synthetic reason", r.Version), default))).Errors));
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AspNetUserRoles WHERE UserId={s.Patients[0]}");
        Assert.Equal(VisitReviewErrors.Forbidden, Assert.Single((await Call(s.Patients[0], false, x => x.OwnAsync(r.AppointmentId, default))).Errors));
    }

    [SqlServerFact]
    public async Task ConcurrentEditsUseVersionsAndModerationPreservesEverySnapshot()
    {
        var s = await Completed(); var created = (await Write(s)).Value.Review;
        var edits = await Task.WhenAll(Write(s, stars: 2, version: created.Version, edit: true), Write(s, stars: 4, version: created.Version, edit: true));
        Assert.Single(edits, x => x.IsSuccess); Assert.Equal(VisitReviewErrors.Stale, Assert.Single(Assert.Single(edits, x => x.IsError).Errors));
        var hidden = (await Moderate(Assert.Single(edits, x => x.IsSuccess).Value.Review)).Value;
        Assert.True(hidden.Hidden); Assert.Null((await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        var edited = (await Write(s, stars: 5, text: "Private information removed", edit: true, version: hidden.Version)).Value.Review;
        Assert.True(edited.Hidden); Assert.Empty((await Call(null, false, x => x.PublicAsync(s.Session.DoctorProfileId, 1, 20, default))).Value.Items);
        var restored = (await Moderate(edited, false)).Value; Assert.False(restored.Hidden);
        Assert.Equal(5, (await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        var history = (await Call(fixture.Admin.Id, true, x => x.HistoryAsync(restored.Id, 1, 20, default))).Value;
        Assert.Equal(new[] { "Create", "Edit", "Hide", "Edit", "Restore" }, history.Items.Select(x => x.Action));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, history.Items.Select(x => x.Revision));
        Assert.Contains("Synthetic review", history.Items[1].BeforeJson); Assert.Equal(s.Patients[0], history.Items[3].ActorUserId);
        Assert.Equal(fixture.Admin.Id, history.Items[4].ActorUserId);
    }

    [SqlServerFact]
    public async Task ExactDeadlineRejectsBothCreationAndEditingWithoutAppendingHistory()
    {
        var s = await Completed(2); var r = (await Write(s)).Value.Review;
        fixture.Database.Clock.UtcNow = s.Session.StartsAtUtc.AddMinutes(10).AddDays(30);
        Assert.Equal(VisitReviewErrors.Expired, Assert.Single((await Write(s, stars: 5, edit: true, version: r.Version)).Errors));
        Assert.Equal(VisitReviewErrors.Expired, Assert.Single((await Write(s, index: 1)).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.VisitReviewAudits.CountAsync(a => a.ReviewId == r.Id));
    }

    [SqlServerFact]
    public async Task CompletionCorrectionSuspendsPublicationAndAggregateWithoutErasingReview()
    {
        var s = await Completed(2); var first = (await Write(s, stars: 1)).Value.Review; await Write(s, index: 1, stars: 4);
        Assert.Equal(2.5, (await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        var row = await attendance.Current(s.Doctor, s, first.AppointmentId);
        Assert.True((await attendance.Write(s.Doctor, s, row, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Completion entered incorrectly")).IsSuccess);
        Assert.Equal(4, (await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        Assert.Equal(1, (await Call(null, false, x => x.PublicAsync(s.Session.DoctorProfileId, 1, 20, default))).Value.TotalCount);
        Assert.False((await Call(s.Patients[0], false, x => x.OwnAsync(first.AppointmentId, default))).Value.Eligible);
        Assert.Equal(VisitReviewErrors.Ineligible, Assert.Single((await Write(s, edit: true, stars: 5, version: first.Version)).Errors));
        var current = await attendance.Current(s.Doctor, s, first.AppointmentId);
        Assert.True((await attendance.Write(s.Doctor, s, current, AttendanceAction.Complete, completed: s.Session.StartsAtUtc.AddMinutes(10))).IsSuccess);
        Assert.Equal(2.5, (await Profile(s.Session.DoctorProfileId)).Doctor.ReviewAverage);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.VisitReviewAudits.CountAsync(a => a.ReviewId == first.Id));
    }

    [SqlServerFact]
    public async Task SearchPlaceAndSessionUseVisibleEligibleDoctorWideAggregateAndPublicOutputHasNoIdentity()
    {
        var s = await Completed(); await Write(s, stars: 3); var schedule = new SchedulingTests(fixture);
        var next = (await schedule.Extra(s.Doctor, s.Session.LocationId, schedule.Tomorrow, 18, 20)).Value;
        await fixture.AsAsync(null, async p =>
        {
            var directory = p.GetRequiredService<IPublicDirectoryService>();
            var search = (await directory.SearchAsync(new() { SpecialtyId = fixture.SpecialtyId, Latitude = 30, Longitude = 31, Sort = DirectorySort.Distance }, default)).Value;
            Assert.Equal(3, Assert.Single(search.Items, r => r.Doctor.Id == s.Session.DoctorProfileId).Doctor.ReviewAverage);
            Assert.Equal(1, (await directory.LocationAsync(s.Session.LocationId, new(), default)).Value.Doctor.ReviewCount);
            Assert.Equal(3, (await directory.SessionAsync(next.Id, default)).Value.Doctor.ReviewAverage); return true;
        });
        var json = JsonSerializer.Serialize((await Call(null, false, x => x.PublicAsync(s.Session.DoctorProfileId, 1, 20, default))).Value);
        foreach (var secret in new[] { "PatientId", "PatientUserId", "AppointmentId", "ActorUserId", "Version", "Email", "Phone", s.Patients[0].ToString() })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task FeedbackClaimDoesNotCreateReviewEligibility()
    {
        var s = await attendance.Setup(); fixture.Database.Clock.UtcNow = s.Session.EndsAtUtc;
        Assert.True((await attendance.Write(s.Doctor, s, s.Rows[0], AttendanceAction.NoShow)).IsSuccess);
        await fixture.AsAsync(s.Patients[0], async p =>
        {
            (p.GetRequiredService<IHttpContextAccessor>().HttpContext!.User.Identity as ClaimsIdentity)!.AddClaim(new("client", "Mobile"));
            var service = p.GetRequiredService<ClinIQ.Application.Features.AttendanceFeedback.IAttendanceFeedbackService>();
            var feedback = Assert.Single((await service.ListAsync(1, 100, default)).Value.Items);
            Assert.True((await service.AnswerAsync(feedback.Id, new(Guid.NewGuid(), ClinIQ.Domain.AttendanceFeedback.PatientAttendanceOutcome.AttendedAndSeen, feedback.Version), false, default)).IsSuccess);
            return true;
        });
        Assert.Equal(VisitReviewErrors.Ineligible, Assert.Single((await Write(s)).Errors));
    }

    [SqlServerFact]
    public async Task DatabaseConstraintsImmutableHistoryRollbackGuardAndUnicodeSnapshotsProtectStoredEvidence()
    {
        var s = await Completed(); var r = (await Write(s, text: new string('ع', 2000))).Value.Review;
        r = (await Call(fixture.Admin.Id, true, x => x.ModerateAsync(r.Id,
            new(true, ReviewModerationCategory.PrivateInformation, new string('ش', 2000), r.Version), default))).Value;
        r = (await Write(s, text: new string('ب', 2000), edit: true, version: r.Version)).Value.Review;
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(51016, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE VisitReviewAudits SET Action='Edit' WHERE ReviewId={r.Id}"))).Number);
        Assert.Equal(51016, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM VisitReviewAudits WHERE ReviewId={r.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE VisitReviews SET Stars=6 WHERE Id={r.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE VisitReviews SET PatientUserId={fixture.Admin.Id} WHERE Id={r.Id}"))).Number);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO VisitReviews (Id,AppointmentId,PatientId,PatientUserId,DoctorProfileId,LocationId,LocationName,Stars,CreatedAtUtc,Hidden,Revision) SELECT {Guid.NewGuid()},AppointmentId,PatientId,PatientUserId,DoctorProfileId,LocationId,LocationName,Stars,CreatedAtUtc,0,1 FROM VisitReviews WHERE Id={r.Id}"));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        var migrator = db.GetService<IMigrator>();
        try { Assert.Equal(51017, (await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261029160441_FeedbackNotifications"))).Number); }
        finally { await migrator.MigrateAsync(); }
        Assert.Equal(3, await db.VisitReviewAudits.CountAsync(x => x.ReviewId == r.Id));
        Assert.NotEmpty(await db.VisitReviewAudits.Where(x => x.ReviewId == r.Id && x.AfterJson.Length > 16000).ToArrayAsync());
    }

    [SqlServerFact]
    public async Task AuditInsertFailureRollsBackTheReview()
    {
        var s = await Completed(); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_Test_ReviewFailure ON VisitReviewAudits AFTER INSERT AS THROW 51999, 'Synthetic review audit failure', 1;");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Write(s));
            Assert.False(await db.VisitReviews.AnyAsync(r => r.AppointmentId == s.Rows[0].Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_Test_ReviewFailure;"); }
    }
}
