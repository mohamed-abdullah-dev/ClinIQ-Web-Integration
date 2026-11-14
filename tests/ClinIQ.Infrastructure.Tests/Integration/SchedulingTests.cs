using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Scheduling;
using ClinIQ.Infrastructure.Tests.Scheduling;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SchedulingTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    internal CairoScheduleClock Cairo => fixture.Services.GetRequiredService<CairoScheduleClock>();
    internal DateOnly Tomorrow => Cairo.Today.AddDays(1);
    [SqlServerFact]
    public async Task PreviewWritesNothingAndRepeatedPublicationPreservesConcreteOccurrences()
    {
        var (d, p) = await Place(); var rule = (await Rule(d, p.Id, Tomorrow)).Value;
        var preview = (await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow.AddDays(14), true, default))).Value;
        Assert.True(preview.IsPreview); Assert.Equal(0, preview.Created); Assert.Equal(3, preview.Planned); Assert.All(preview.Items, x => Assert.Null(x.SessionId));
        Assert.Equal(0, (await Sessions(d)).TotalCount);
        var first = (await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow.AddDays(14), false, default))).Value; Assert.Equal(3, first.Created);
        var second = (await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow.AddDays(14), false, default))).Value; Assert.Equal(0, second.Created); Assert.Equal(3, second.Preserved);
        var rows = (await Sessions(d)).Items; Assert.All(rows, x => { Assert.Equal(rule.Id, x.SourceRuleId); Assert.Equal(rule.Version, x.SourceRuleVersion); Assert.Equal("Africa/Cairo", x.TimeZoneId); Assert.Equal(x.EndsAtUtc.AddMinutes(-30), x.BookingClosesAtUtc); });
    }
    [SqlServerFact]
    public async Task TemplateChangesNeverRewritePublishedSettingsOrOperationalOverrides()
    {
        var (d, p) = await Place(); var rule = (await Rule(d, p.Id, Tomorrow)).Value; await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow, false, default));
        var first = Assert.Single((await Sessions(d)).Items); var paused = (await Edit(d, first, SessionEditKind.Pause)).Value;
        var changed = (await Call(d, s => s.UpdateRuleAsync(rule.Id, rule.Data with { MaxCapacity = 20, BookingCutoffMinutes = 0, StartsLocal = new(19, 0), EndsLocal = new(22, 0) }, rule.Version, default))).Value;
        var publish = (await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow.AddDays(7), false, default))).Value; Assert.Equal(1, publish.Preserved); Assert.Equal(1, publish.Created);
        var kept = (await Call(d, s => s.GetSessionAsync(first.Id, default))).Value; Assert.Equal(paused, kept); Assert.Equal(rule.Version, kept.SourceRuleVersion);
        var next = (await Sessions(d)).Items.Single(x => x.Id != kept.Id); Assert.Equal(20, next.MaxCapacity); Assert.Equal(0, next.BookingCutoffMinutes); Assert.Equal(changed.Version, next.SourceRuleVersion);
    }
    [SqlServerFact]
    public async Task OverlapIsDoctorWideAcrossPlacesAndTouchingBoundariesAreAllowed()
    {
        var (d, p) = await Place(); var other = await SecondPlace(d); var a = (await Extra(d, p.Id, Tomorrow, 18, 20)).Value;
        Assert.Equal(ScheduleErrors.Overlap, Assert.Single((await Extra(d, other.Id, Tomorrow, 19, 21)).Errors));
        Assert.True((await Extra(d, other.Id, Tomorrow, 20, 22)).IsSuccess);
        Assert.Equal(ScheduleErrors.Overlap, Assert.Single((await Edit(d, a, SessionEditKind.Times, start: new(19, 0), end: new(21, 0))).Errors));
    }
    [SqlServerFact]
    public async Task WeeklyRuleOverlapUsesEffectiveDatesAcrossAllLocations()
    {
        var (d, p) = await Place(); var other = await SecondPlace(d); Assert.True((await Rule(d, p.Id, Tomorrow)).IsSuccess);
        Assert.Equal(ScheduleErrors.Overlap, Assert.Single((await Rule(d, other.Id, Tomorrow)).Errors));
        Assert.True((await Call(d, s => s.CreateRuleAsync(other.Id, Input(Tomorrow) with { StartsLocal = new(20, 0), EndsLocal = new(22, 0) }, default))).IsSuccess);
    }
    [SqlServerFact]
    public async Task ConcurrentExtraSessionsAcrossTwoPlacesCannotPublishAnOverlap()
    {
        var (d, p) = await Place(); var other = await SecondPlace(d);
        var results = await Task.WhenAll(Extra(d, p.Id, Tomorrow, 18, 20), Extra(d, other.Id, Tomorrow, 19, 21));
        Assert.Single(results, x => x.IsSuccess); Assert.Equal(ScheduleErrors.Overlap, Assert.Single(Assert.Single(results, x => x.IsError).Errors)); Assert.Single((await Sessions(d)).Items);
    }
    [SqlServerFact]
    public async Task ConcurrentPublicationOfTheSameOccurrenceIsIdempotent()
    {
        var (d, p) = await Place(); await Rule(d, p.Id, Tomorrow);
        var results = await Task.WhenAll(Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow, false, default)), Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow, false, default)));
        Assert.All(results, x => Assert.True(x.IsSuccess)); Assert.Equal(1, results.Sum(x => x.Value.Created)); Assert.Single((await Sessions(d)).Items);
    }
    [SqlServerFact]
    public async Task HolidayScopesCancelOnlyUnstartedUnallocatedOccurrencesAndRemovalDoesNotResurrectThem()
    {
        var (d, p) = await Place(); var other = await SecondPlace(d); var a = (await Extra(d, p.Id, Tomorrow, 18, 20)).Value; var b = (await Extra(d, other.Id, Tomorrow, 20, 22)).Value;
        var holiday = (await Call(d, s => s.CreateExceptionAsync(p.Id, Tomorrow, "This place is closed", default))).Value;
        Assert.Equal("Cancelled", (await Call(d, s => s.GetSessionAsync(a.Id, default))).Value.Status); Assert.Equal("Published", (await Call(d, s => s.GetSessionAsync(b.Id, default))).Value.Status);
        Assert.Equal(ScheduleErrors.Holiday, Assert.Single((await Extra(d, p.Id, Tomorrow, 16, 17)).Errors));
        Assert.True((await Call(d, s => s.RemoveExceptionAsync(holiday.Id, holiday.Version, "Reopened", default))).IsSuccess);
        Assert.Equal("Cancelled", (await Call(d, s => s.GetSessionAsync(a.Id, default))).Value.Status); Assert.True((await Extra(d, p.Id, Tomorrow, 18, 20)).IsSuccess);
        Assert.True((await Call(d, s => s.CreateExceptionAsync(null, Tomorrow, "Doctor unavailable", default))).IsSuccess);
        Assert.All((await Sessions(d)).Items, x => Assert.Equal("Cancelled", x.Status));
    }
    [SqlServerFact]
    public async Task AllocationsBlockAutomaticHolidayCancellationAndTimeMovesButAllowSafeCapacity()
    {
        var (d, p) = await Place(); var row = (await Extra(d, p.Id, Tomorrow, 18, 20)).Value;
        // P08 does not exist yet: seed only the future authoritative allocation counter in this isolated DB.
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE BookingSessions SET AllocatedCount=2 WHERE Id={row.Id}"); row = (await Call(d, s => s.GetSessionAsync(row.Id, default))).Value;
        Assert.Equal(ScheduleErrors.Capacity, Assert.Single((await Edit(d, row, SessionEditKind.Capacity, value: 1)).Errors));
        Assert.Equal(ScheduleErrors.Allocations, Assert.Single((await Edit(d, row, SessionEditKind.Times, start: new(17, 0), end: new(19, 0))).Errors));
        Assert.Equal(ScheduleErrors.Allocations, Assert.Single((await Call(d, s => s.CreateExceptionAsync(p.Id, Tomorrow, "Cannot erase allocations", default))).Errors));
        Assert.False(await db.ScheduleExceptions.AnyAsync(x => x.DoctorProfileId == row.DoctorProfileId)); Assert.Equal("Published", (await Call(d, s => s.GetSessionAsync(row.Id, default))).Value.Status);
        Assert.True((await Edit(d, row, SessionEditKind.Capacity, value: 2)).IsSuccess);
    }
    [SqlServerFact]
    public async Task CutoffChangesDuringSessionReopenTimeButRespectPauseEndAndCapacity()
    {
        var (d, p) = await Place(); var row = (await Extra(d, p.Id, Tomorrow, 18, 20, cutoff: 60)).Value; fixture.Database.Clock.UtcNow = row.EndsAtUtc.AddMinutes(-30);
        Assert.False((await Call(d, s => s.GetSessionAsync(row.Id, default))).Value.AcceptingNewBookings);
        row = (await Edit(d, row, SessionEditKind.Cutoff, value: 0)).Value; Assert.True(row.AcceptingNewBookings);
        row = (await Edit(d, row, SessionEditKind.Pause)).Value; row = (await Edit(d, row, SessionEditKind.Cutoff, value: 15)).Value; Assert.True(row.BookingPaused); Assert.False(row.AcceptingNewBookings);
        row = (await Edit(d, row, SessionEditKind.Resume)).Value; Assert.True(row.AcceptingNewBookings);
        fixture.Database.Clock.UtcNow = row.BookingClosesAtUtc; Assert.False((await Call(d, s => s.GetSessionAsync(row.Id, default))).Value.AcceptingNewBookings);
        Assert.Equal(ScheduleErrors.Closed, Assert.Single((await Edit(d, row, SessionEditKind.Resume)).Errors));
        Assert.Equal(ScheduleErrors.Started, Assert.Single((await Edit(d, row, SessionEditKind.Times, start: new(17, 0), end: new(21, 0))).Errors));
        fixture.Database.Clock.UtcNow = row.EndsAtUtc; Assert.Equal(ScheduleErrors.Ended, Assert.Single((await Edit(d, row, SessionEditKind.Capacity, value: 20)).Errors));
    }
    [SqlServerFact]
    public async Task WrongDoctorsAndNonDoctorRolesCannotInspectOrChangeSchedules()
    {
        var (d, p) = await Place(); var rule = (await Rule(d, p.Id, Tomorrow)).Value; var row = (await Extra(d, p.Id, Tomorrow, 15, 16)).Value; var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        Assert.Equal(ScheduleErrors.NotFound, Assert.Single((await Call(other.UserId, s => s.GetRuleAsync(rule.Id, default))).Errors));
        Assert.Equal(ScheduleErrors.NotFound, Assert.Single((await Edit(other.UserId, row, SessionEditKind.Pause)).Errors));
        Assert.Equal(ScheduleErrors.Forbidden, Assert.Single((await Call(fixture.Admin.Id, s => s.GetSessionAsync(row.Id, default))).Errors));
        var ghost = await fixture.UserAsync("Receptionist"); Assert.Equal(ScheduleErrors.Forbidden, Assert.Single((await Call(ghost.Id, s => s.CreateRuleAsync(p.Id, Input(Tomorrow), default))).Errors));
        Assert.Equal(ScheduleErrors.Unauthorized, Assert.Single((await Call(null, s => s.GetSessionAsync(row.Id, default))).Errors));
    }
    [SqlServerFact]
    public async Task HorizonInvalidTimesAndUnavailableLocationsCannotPublish()
    {
        var (d, p) = await Place(); await Rule(d, p.Id, Tomorrow);
        Assert.Equal(ScheduleErrors.Horizon, Assert.Single((await Call(d, s => s.PublishAsync(p.Id, Cairo.Today, Cairo.Today.AddDays(28), false, default))).Errors));
        Assert.Equal(ScheduleErrors.LocalTimes, Assert.Single((await Extra(d, p.Id, Tomorrow, 22, 18)).Errors));
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET IsActive=0 WHERE Id={p.Id}");
        Assert.Equal(ScheduleErrors.Unavailable, Assert.Single((await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow, false, default))).Errors));
    }
    [SqlServerFact]
    public async Task AHolidayCanSuppressAnInvalidDstOccurrenceWithoutInventingAUtcInstant()
    {
        var (d, p) = await Place(); var gap = CairoScheduleClockTests.FindTransition(Cairo.Zone, false); var date = DateOnly.FromDateTime(gap); fixture.Database.Clock.UtcNow = new DateTimeOffset(gap.AddDays(-2), TimeSpan.Zero);
        var rule = Input(date) with { StartsLocal = TimeOnly.FromDateTime(gap), EndsLocal = TimeOnly.FromDateTime(gap).AddHours(2) };
        Assert.True((await Call(d, s => s.CreateRuleAsync(p.Id, rule, default))).IsSuccess);
        Assert.Equal(ScheduleErrors.TimeConversion, Assert.Single((await Call(d, s => s.PublishAsync(p.Id, date, date, true, default))).Errors));
        Assert.True((await Call(d, s => s.CreateExceptionAsync(p.Id, date, "Holiday on DST transition", default))).IsSuccess);
        var result = (await Call(d, s => s.PublishAsync(p.Id, date, date, false, default))).Value; Assert.Equal(1, result.Suppressed); Assert.Equal(0, result.Created); Assert.Null(Assert.Single(result.Items).StartsAtUtc);
    }
    [SqlServerFact]
    public async Task StaleVersionsAndLostEligibilityStopWrites()
    {
        var (d, p) = await Place(); var row = (await Extra(d, p.Id, Tomorrow, 18, 20)).Value; Assert.True((await Edit(d, row, SessionEditKind.Pause)).IsSuccess);
        Assert.Equal(ScheduleErrors.Stale, Assert.Single((await Edit(d, row, SessionEditKind.Capacity, value: 5)).Errors));
        await using var db = fixture.Database.CreateContext(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET EmailConfirmed=0 WHERE Id={d}");
        Assert.Equal(ScheduleErrors.Forbidden, Assert.Single((await Rule(d, p.Id, Tomorrow)).Errors));
    }
    [SqlServerFact]
    public async Task PublicationFailureRollsBackAllNewSessions()
    {
        var (d, p) = await Place(); await Rule(d, p.Id, Tomorrow); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_ScheduleTest_FailPublish ON dbo.BookingSessions AFTER INSERT AS THROW 51059, 'Synthetic session save failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow.AddDays(14), false, default))); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_ScheduleTest_FailPublish;"); }
        Assert.False(await db.BookingSessions.AnyAsync(x => x.UserId == d));
    }
    [SqlServerFact]
    public async Task SqlEnforcesScopeOccurrenceAndCapacityAndDownPreservesHistory()
    {
        var (d, p) = await Place(); var rule = (await Rule(d, p.Id, Tomorrow)).Value; await Call(d, s => s.PublishAsync(p.Id, Tomorrow, Tomorrow, false, default)); var extra = (await Extra(d, p.Id, Tomorrow, 15, 16)).Value;
        await using var db = fixture.Database.CreateContext();
        var invalid = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE BookingSessions SET AllocatedCount=MaxCapacity+1 WHERE Id={extra.Id}")); Assert.Equal(547, invalid.Number);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE BookingSessions SET SourceRuleId={rule.Id},SourceRuleVersion=(SELECT Version FROM ScheduleRules WHERE Id={rule.Id}) WHERE Id={extra.Id}")); Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261028161807_DoctorScheduling");
        try { var down = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028152615_WorkInvitations")); Assert.Equal(51005, down.Number); } finally { await migrator.MigrateAsync(); }
    }
    [SqlServerFact]
    public async Task InvitedDoctorCanPublishAtAManagedBranchButInactiveBranchStopsAvailability()
    {
        var (doctor, _) = await Place();
        var (manager, _, branch) = await new PremisesCorrectionTests(fixture).Branch();
        await using var db = fixture.Database.CreateContext();
        var email = await db.Users.Where(x => x.Id == doctor).Select(x => x.Email).SingleAsync();
        var invitations = new WorkInvitationTests(fixture);
        var invitation = (await invitations.Issue(manager, ClinIQ.Domain.Invitations.WorkInvitationKind.Doctor, branch.Id, null, email!, 150)).Value;
        Assert.True((await invitations.Respond(doctor, invitation)).IsSuccess);
        var locationId = await db.DoctorPracticeLocations.Where(x => x.UserId == doctor && x.ClinicBranchId == branch.Id).Select(x => x.Id).SingleAsync();
        Assert.True((await Rule(doctor, locationId, Tomorrow)).IsSuccess);
        Assert.Equal(1, (await Call(doctor, s => s.PublishAsync(locationId, Tomorrow, Tomorrow, false, default))).Value.Created);
        var session = Assert.Single((await Sessions(doctor)).Items);
        Assert.True(session.ResourcesAvailable); Assert.True(session.AcceptingNewBookings);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET IsActive=0 WHERE Id={branch.Id}");
        var unavailable = (await Call(doctor, s => s.GetSessionAsync(session.Id, default))).Value;
        Assert.False(unavailable.ResourcesAvailable); Assert.False(unavailable.AcceptingNewBookings);
        Assert.Equal(ScheduleErrors.Unavailable, Assert.Single((await Call(doctor, s => s.PublishAsync(locationId, Tomorrow, Tomorrow, false, default))).Errors));
        Assert.Equal(ScheduleErrors.Closed, Assert.Single((await Edit(doctor, session, SessionEditKind.Resume)).Errors));
    }
    [SqlServerFact]
    public async Task ConcurrentCapacityWritesAndAllocationCounterNeverExceedThePublishedMaximum()
    {
        var (d, p) = await Place(); var row = (await Extra(d, p.Id, Tomorrow, 18, 20, capacity: 2)).Value;
        // Simulate P08's required session lock with a real competing SQL writer, not a patient booking API.
        var allocation = Task.Run(async () =>
        {
            await using var db = fixture.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync(); var resource = $"ClinIQ.BookingSession.{row.Id:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource={resource},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; IF @r<0 THROW 51069,'Synthetic allocation lock failed',1; UPDATE BookingSessions SET AllocatedCount=2 WHERE Id={row.Id} AND MaxCapacity>=2;"); await tx.CommitAsync();
        });
        var edit = Edit(d, row, SessionEditKind.Capacity, value: 1); await Task.WhenAll(allocation, edit); var state = (await Call(d, s => s.GetSessionAsync(row.Id, default))).Value; Assert.InRange(state.AllocatedCount, 0, state.MaxCapacity);
    }
    internal Task<T> Call<T>(Guid? actor, Func<ISchedulingService, Task<T>> run) => fixture.AsAsync(actor, p => run(p.GetRequiredService<ISchedulingService>()));
    internal async Task<(Guid Doctor, PracticeLocationDto Place)> Place() { fixture.Database.Clock.UtcNow = SqlServerIntegrationFixture.InitialUtc; return await new PremisesCorrectionTests(fixture).Place(); }
    internal async Task<PracticeLocationDto> SecondPlace(Guid owner)
    {
        var draft = await fixture.AsAsync(owner, async p => (await p.GetRequiredService<IPracticeLocationService>().CreateAsync(new("Other place", "Other address", null, null, 200, "EGP"), default)).Value);
        var docs = PracticeLocationTestData.Uploads(); PracticeLocationDto pending;
        try { pending = await fixture.AsAsync(owner, async p => (await p.GetRequiredService<IPracticeLocationService>().SubmitAsync(draft.Id, draft.Version, docs, default)).Value); } finally { foreach (var f in docs) await f.Content.DisposeAsync(); }
        return await fixture.AsAsync(fixture.Admin.Id, async p => (await p.GetRequiredService<IPracticeLocationService>().ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value, PracticeLocationStatus.Approved, "Verified", pending.Version, true, true, default)).Value);
    }
    internal ScheduleRuleData Input(DateOnly date) => new(date.DayOfWeek, new(18, 0), new(20, 0), 10, 30, date, null);
    internal Task<Result<ScheduleRuleDto>> Rule(Guid d, Guid location, DateOnly date) => Call(d, s => s.CreateRuleAsync(location, Input(date), default));
    internal Task<Result<SessionDto>> Extra(Guid d, Guid location, DateOnly date, int start, int end, int capacity = 10, int cutoff = 0) => Call(d, s => s.ExtraAsync(location, date, new(start, 0), new(end, 0), capacity, cutoff, default));
    internal Task<Result<SessionDto>> Edit(Guid d, SessionDto row, SessionEditKind kind, int? value = null, TimeOnly? start = null, TimeOnly? end = null) => Call(d, s => s.EditSessionAsync(row.Id, kind, row.Version, start, end, value, "Synthetic session reason", default));
    private Task<SchedulePage<SessionDto>> Sessions(Guid d) => Call(d, async s => (await s.SessionsAsync(null, Cairo.Today, Cairo.Today.AddDays(27), 1, 100, default)).Value);
}
