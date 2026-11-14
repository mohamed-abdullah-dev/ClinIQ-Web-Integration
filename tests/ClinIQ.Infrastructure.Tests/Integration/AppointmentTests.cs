using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Patients;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AppointmentTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private SchedulingTests Schedule => new(fixture);
    internal Task<T> Call<T>(Guid? actor, Func<IAppointmentService, Task<T>> run) => fixture.AsAsync(actor, p => run(p.GetRequiredService<IAppointmentService>()));
    internal async Task<Guid> Patient()
    {
        var user = await fixture.UserAsync("Patient");
        await fixture.AsAsync(user.Id, async p => { Assert.True((await p.GetRequiredService<IPatientProfileService>().SaveOwnAsync("Synthetic Patient", "01012345678", null, default)).IsSuccess); return true; });
        return user.Id;
    }
    private async Task<(Guid Doctor, Guid Patient, SessionDto Session)> Setup(int capacity = 10)
    { var (d, p) = await Schedule.Place(); return (d, await Patient(), (await Schedule.Extra(d, p.Id, Schedule.Tomorrow, 18, 20, capacity)).Value); }
    internal Task<Result<BookingPreview>> Preview(Guid patient, Guid session, Guid? source = null) => Call(patient, s => s.PreviewAsync(session, source, default));
    internal static BookingConfirmation Accept(BookingPreview p, bool overlap = false) => new(p.Selection.SessionId, p.QuoteToken, Guid.NewGuid(), true, true, overlap);
    internal async Task<Result<AppointmentOperationResult>> Book(Guid patient, Guid session)
    { var preview = (await Preview(patient, session)).Value; return await Call(patient, s => s.BookAsync(Accept(preview), default)); }

    [SqlServerFact]
    public async Task RealBookingPersistsSelfOwnedSnapshotsAllocationAndDurableNotices()
    {
        var (doctor, patient, session) = await Setup(); var preview = (await Preview(patient, session.Id)).Value;
        var input = Accept(preview); var row = (await Call(patient, s => s.BookAsync(input, default))).Value.Appointment;
        Assert.Equal(preview.Selection, row.Selection); Assert.Equal("Confirmed", row.Status);
        await using var db = fixture.Database.CreateContext(); var saved = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == row.Id);
        Assert.Equal(patient, saved.PatientUserId); Assert.Equal(doctor, saved.DoctorUserId); Assert.Equal(patient, saved.CreatedByUserId);
        Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount);
        var notices = await db.BookingNotices.Where(n => n.AppointmentId == row.Id).ToArrayAsync(); Assert.Equal(2, notices.Length); Assert.All(notices, n => Assert.Equal("DevelopmentFile", n.DeliveryStatus));
        var operation = await db.AppointmentOperations.SingleAsync(o => o.ResultAppointmentId == row.Id); Assert.Equal(64, operation.RequestHash.Length); Assert.DoesNotContain(input.QuoteToken, operation.RequestHash);
    }
    [SqlServerFact]
    public async Task RepeatedAndConcurrentSameRequestCreatesExactlyOneAppointmentAndReplaySurvivesQuoteExpiry()
    {
        var (_, patient, session) = await Setup(); var input = Accept((await Preview(patient, session.Id)).Value);
        var tasks = Enumerable.Range(0, 4).Select(_ => Call(patient, s => s.BookAsync(input, default))).ToArray(); var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.IsSuccess)); Assert.Single(results, r => !r.Value.Replayed);
        Assert.Single(results.Select(r => r.Value.Appointment.Id).Distinct());
        fixture.Database.Clock.UtcNow = fixture.Database.Clock.GetUtcNow().AddHours(1);
        Assert.True((await Call(patient, s => s.BookAsync(input, default))).Value.Replayed);
        Assert.Equal(AppointmentErrors.RequestReuse, Assert.Single((await Call(patient, s => s.BookAsync(input with { AcknowledgeOverlap = true }, default))).Errors));
    }
    [SqlServerFact]
    public async Task ConcurrentPatientsCompeteForTheLastPlaceWithoutOverselling()
    {
        var (_, patient, session) = await Setup(1); var other = await Patient();
        var first = Accept((await Preview(patient, session.Id)).Value); var second = Accept((await Preview(other, session.Id)).Value);
        var results = await Task.WhenAll(Call(patient, s => s.BookAsync(first, default)), Call(other, s => s.BookAsync(second, default)));
        Assert.Single(results, r => r.IsSuccess); Assert.Equal(AppointmentErrors.Closed, Assert.Single(Assert.Single(results, r => r.IsError).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.Appointments.CountAsync(a => a.SessionId == session.Id)); Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount);
    }
    [SqlServerFact]
    public async Task DuplicateReservationIsBlockedEvenWithANewRequestId()
    {
        var (_, patient, session) = await Setup(); var input = Accept((await Preview(patient, session.Id)).Value);
        Assert.True((await Call(patient, s => s.BookAsync(input, default))).IsSuccess);
        Assert.Equal(AppointmentErrors.Duplicate, Assert.Single((await Call(patient, s => s.BookAsync(input with { RequestId = Guid.NewGuid() }, default))).Errors));
    }
    [SqlServerTheory]
    [InlineData("price")]
    [InlineData("contact")]
    [InlineData("time")]
    [InlineData("address")]
    public async Task ChangedAcceptedDetailsRequireANewPreview(string change)
    {
        var (doctor, patient, session) = await Setup(); var input = Accept((await Preview(patient, session.Id)).Value);
        await using var db = fixture.Database.CreateContext();
        switch (change)
        {
            case "price": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET ConsultationFee=125 WHERE Id={session.LocationId}"); break;
            case "contact": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PatientProfiles SET FullName='Changed contact' WHERE UserId={patient}"); break;
            case "time": await Schedule.Edit(doctor, session, SessionEditKind.Times, start: new(19, 0), end: new(21, 0)); break;
            default: await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET Address='Corrected address' WHERE Id={session.LocationId}"); break;
        }
        Assert.Equal(AppointmentErrors.SelectionChanged, Assert.Single((await Call(patient, s => s.BookAsync(input, default))).Errors));
        Assert.False(await db.Appointments.AnyAsync(a => a.SessionId == session.Id));
        var fresh = (await Preview(patient, session.Id)).Value;
        Assert.Equal(fresh.Selection, (await Call(patient, s => s.BookAsync(Accept(fresh), default))).Value.Appointment.Selection);
    }
    [SqlServerFact]
    public async Task ConsentQuoteBindingExpiryAndLiveEligibilityAreEnforced()
    {
        var (doctor, patient, session) = await Setup(); var input = Accept((await Preview(patient, session.Id)).Value); var other = await Patient();
        Assert.Equal(AppointmentErrors.Acceptance, Assert.Single((await Call(patient, s => s.BookAsync(input with { AcceptPrice = false }, default))).Errors));
        Assert.Equal(AppointmentErrors.Quote, Assert.Single((await Call(other, s => s.BookAsync(input, default))).Errors));
        Assert.Equal(AppointmentErrors.Quote, Assert.Single((await Call(patient, s => s.BookAsync(input with { QuoteToken = "tampered" }, default))).Errors));
        fixture.Database.Clock.UtcNow = fixture.Database.Clock.GetUtcNow().AddMinutes(11);
        Assert.Equal(AppointmentErrors.Quote, Assert.Single((await Call(patient, s => s.BookAsync(input, default))).Errors));
        var fresh = Accept((await Preview(patient, session.Id)).Value); await Schedule.Edit(doctor, session, SessionEditKind.Pause);
        Assert.Equal(AppointmentErrors.Closed, Assert.Single((await Call(patient, s => s.BookAsync(fresh, default))).Errors));
    }
    [SqlServerFact]
    public async Task OverlapRequiresExplicitAcknowledgementAndConcurrentChangesInvalidateOldWarning()
    {
        var (_, patient, first) = await Setup(); var (otherDoctor, otherPlace) = await Schedule.Place();
        var second = (await Schedule.Extra(otherDoctor, otherPlace.Id, Schedule.Tomorrow, 19, 21)).Value;
        var old = Accept((await Preview(patient, second.Id)).Value);
        Assert.True((await Book(patient, first.Id)).IsSuccess);
        Assert.Equal(AppointmentErrors.SelectionChanged, Assert.Single((await Call(patient, s => s.BookAsync(old, default))).Errors));
        var preview = (await Preview(patient, second.Id)).Value; Assert.Single(preview.Overlaps); Assert.NotNull(preview.Warning);
        Assert.Equal(AppointmentErrors.Overlap, Assert.Single((await Call(patient, s => s.BookAsync(Accept(preview), default))).Errors));
        Assert.True((await Call(patient, s => s.BookAsync(Accept(preview, true), default))).IsSuccess);
    }
    [SqlServerFact]
    public async Task CancellationAfterCutoffReleasesOnceAndDoesNotResumeAPausedSession()
    {
        var (doctor, patient, session) = await Setup(); var row = (await Book(patient, session.Id)).Value.Appointment;
        var current = (await Schedule.Call(doctor, s => s.GetSessionAsync(session.Id, default))).Value;
        current = (await Schedule.Edit(doctor, current, SessionEditKind.Cutoff, value: 60)).Value;
        await Schedule.Edit(doctor, current, SessionEditKind.Pause);
        fixture.Database.Clock.UtcNow = current.EndsAtUtc.AddMinutes(-30);
        var request = Guid.NewGuid(); Assert.Equal("Cancelled", (await Call(patient, s => s.CancelAsync(row.Id, request, "Cannot attend", default))).Value.Appointment.Status);
        Assert.True((await Call(patient, s => s.CancelAsync(row.Id, request, "Cannot attend", default))).Value.Replayed);
        Assert.True((await Call(patient, s => s.CancelAsync(row.Id, Guid.NewGuid(), "Repeated", default))).IsSuccess);
        await using var db = fixture.Database.CreateContext(); var saved = await db.BookingSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(0, saved.AllocatedCount); Assert.True(saved.BookingPaused); Assert.Equal(2, await db.BookingNotices.CountAsync(n => n.AppointmentId == row.Id && n.Kind == BookingNoticeKind.Cancelled));
    }
    [SqlServerFact]
    public async Task RescheduleAtomicallyLinksReplacementAndKeepsOldPriceHistory()
    {
        var (doctor, patient, session) = await Setup(); var row = (await Book(patient, session.Id)).Value.Appointment;
        var secondPlace = await Schedule.SecondPlace(doctor); var target = (await Schedule.Extra(doctor, secondPlace.Id, Schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        var input = Accept((await Preview(patient, target.Id, row.Id)).Value);
        var replacement = (await Call(patient, s => s.RescheduleAsync(row.Id, input, default))).Value.Appointment;
        Assert.Equal(row.Id, replacement.ReplacesAppointmentId); Assert.Equal(200, replacement.Selection.Fee);
        var source = (await Call(patient, s => s.GetAsync(row.Id, default))).Value;
        Assert.Equal("Rescheduled", source.Status); Assert.Equal(replacement.Id, source.ReplacedByAppointmentId); Assert.Equal(100, source.Selection.Fee);
        Assert.True((await Call(patient, s => s.RescheduleAsync(row.Id, input, default))).Value.Replayed);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(0, (await db.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount); Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == target.Id)).AllocatedCount);
    }
    [SqlServerFact]
    public async Task FailedOrCrossDoctorRescheduleLeavesOriginalAppointmentAndAllocationIntact()
    {
        var (doctor, patient, session) = await Setup(); var row = (await Book(patient, session.Id)).Value.Appointment;
        Assert.Equal(AppointmentErrors.SameDoctor, Assert.Single((await Preview(patient, session.Id, row.Id)).Errors));
        var (otherDoctor, otherPlace) = await Schedule.Place(); var other = (await Schedule.Extra(otherDoctor, otherPlace.Id, Schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        Assert.Equal(AppointmentErrors.SameDoctor, Assert.Single((await Preview(patient, other.Id, row.Id)).Errors));
        var target = (await Schedule.Extra(doctor, session.LocationId, Schedule.Tomorrow.AddDays(1), 18, 20)).Value; var input = Accept((await Preview(patient, target.Id, row.Id)).Value);
        await Schedule.Edit(doctor, target, SessionEditKind.Pause);
        Assert.Equal(AppointmentErrors.Closed, Assert.Single((await Call(patient, s => s.RescheduleAsync(row.Id, input, default))).Errors));
        Assert.Equal("Confirmed", (await Call(patient, s => s.GetAsync(row.Id, default))).Value.Status);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount);
    }
    [SqlServerFact]
    public async Task SaveFailureRollsBackSourceTargetOperationsAndNotices()
    {
        var (doctor, patient, session) = await Setup(); var row = (await Book(patient, session.Id)).Value.Appointment;
        var target = (await Schedule.Extra(doctor, session.LocationId, Schedule.Tomorrow.AddDays(1), 18, 20)).Value; var input = Accept((await Preview(patient, target.Id, row.Id)).Value);
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_BookingTest_FailOperation ON dbo.AppointmentOperations AFTER INSERT AS THROW 51071, 'Synthetic booking operation failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Call(patient, s => s.RescheduleAsync(row.Id, input, default))); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_BookingTest_FailOperation;"); }
        Assert.Equal("Confirmed", (await Call(patient, s => s.GetAsync(row.Id, default))).Value.Status);
        Assert.False(await db.Appointments.AnyAsync(a => a.ReplacesAppointmentId == row.Id)); Assert.False(await db.AppointmentOperations.AnyAsync(o => o.RequestId == input.RequestId));
        Assert.Equal(0, (await db.BookingSessions.SingleAsync(s => s.Id == target.Id)).AllocatedCount);
        Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount);
        Assert.False(await db.BookingNotices.AnyAsync(n => n.AppointmentId == row.Id && n.Kind == BookingNoticeKind.Rescheduled));
    }
    [SqlServerFact]
    public async Task OwnershipProfileRoleAndEndBoundariesProtectPrivateAppointments()
    {
        var (doctor, patient, session) = await Setup(); var row = (await Book(patient, session.Id)).Value.Appointment; var other = await Patient();
        Assert.Equal(AppointmentErrors.NotFound, Assert.Single((await Call(other, s => s.GetAsync(row.Id, default))).Errors));
        Assert.Equal(AppointmentErrors.NotFound, Assert.Single((await Call(other, s => s.CancelAsync(row.Id, Guid.NewGuid(), "Other", default))).Errors));
        Assert.Equal(AppointmentErrors.Unauthorized, Assert.Single((await Call(null, s => s.ListAsync(1, 20, default))).Errors));
        Assert.Equal(AppointmentErrors.Forbidden, Assert.Single((await Call(doctor, s => s.GetAsync(row.Id, default))).Errors));
        var incomplete = await fixture.UserAsync("Patient"); Assert.Equal("PatientProfile.NotFound", Assert.Single((await Preview(incomplete.Id, session.Id)).Errors).Code);
        fixture.Database.Clock.UtcNow = session.EndsAtUtc;
        Assert.Equal(AppointmentErrors.CannotChange, Assert.Single((await Call(patient, s => s.CancelAsync(row.Id, Guid.NewGuid(), "Too late", default))).Errors));
    }
    [SqlServerFact]
    public async Task DoctorDisruptionCancelsRemainingPatientsButPreservesCompletedWorkloadAndFacts()
    {
        var (doctor, patient, session) = await Setup(); var checkedIn = (await Book(patient, session.Id)).Value.Appointment;
        var other = await Patient(); var completed = (await Book(other, session.Id)).Value.Appointment;
        await using var db = fixture.Database.CreateContext();
        // Synthetic factual snapshots exercise the booking cancellation boundary; attendance transitions have their own suite.
        var arrived = session.StartsAtUtc.AddMinutes(5); var finished = session.StartsAtUtc.AddMinutes(15);
        fixture.Database.Clock.UtcNow = session.StartsAtUtc.AddMinutes(30);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Appointments SET Status=2, QueueNumber=1, ArrivedAtUtc={arrived}, CheckInRecordedAtUtc={arrived} WHERE Id={checkedIn.Id}; UPDATE Appointments SET Status=3, QueueNumber=2, ArrivedAtUtc={arrived}, CheckInRecordedAtUtc={arrived}, CompletedAtUtc={finished}, CompletionRecordedAtUtc={finished} WHERE Id={completed.Id}; UPDATE BookingSessions SET LastQueueNumber=2 WHERE Id={session.Id}");
        Assert.Equal(AppointmentErrors.CannotChange, Assert.Single((await Call(patient, s => s.CancelAsync(checkedIn.Id, Guid.NewGuid(), "Checked in", default))).Errors));
        var current = (await Schedule.Call(doctor, s => s.GetSessionAsync(session.Id, default))).Value;
        var result = (await Call(doctor, s => s.CancelSessionAsync(session.Id, current.Version, "Doctor illness", default))).Value;
        Assert.Equal("Cancelled", result.Status); Assert.Equal(1, result.AllocatedCount);
        var cancelled = (await Call(patient, s => s.GetAsync(checkedIn.Id, default))).Value; Assert.Equal("Cancelled", cancelled.Status); Assert.Equal("CheckedIn", cancelled.PriorStatus);
        Assert.Equal("Completed", (await Call(other, s => s.GetAsync(completed.Id, default))).Value.Status);
        Assert.Equal(0, await db.BookingNotices.CountAsync(n => n.AppointmentId == completed.Id && n.Kind == BookingNoticeKind.Cancelled));
        Assert.Equal(1, (await Call(doctor, s => s.CancelSessionAsync(session.Id, result.Version, "Repeated", default))).Value.AllocatedCount);
    }
    [SqlServerFact]
    public async Task CompletedPatientCannotBookTheSameSessionAgainAndCapacityCannotShrinkBelowAllocations()
    {
        var (doctor, patient, session) = await Setup(2); var row = (await Book(patient, session.Id)).Value.Appointment;
        await using var db = fixture.Database.CreateContext(); var arrived = session.StartsAtUtc.AddMinutes(1); var finished = session.StartsAtUtc.AddMinutes(5);
        fixture.Database.Clock.UtcNow = session.StartsAtUtc.AddMinutes(10);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Appointments SET Status=3, QueueNumber=1, ArrivedAtUtc={arrived}, CheckInRecordedAtUtc={arrived}, CompletedAtUtc={finished}, CompletionRecordedAtUtc={finished} WHERE Id={row.Id}; UPDATE BookingSessions SET LastQueueNumber=1 WHERE Id={session.Id}");
        var fresh = Accept((await Preview(patient, session.Id)).Value);
        Assert.Equal(AppointmentErrors.Duplicate, Assert.Single((await Call(patient, s => s.BookAsync(fresh, default))).Errors));
        var other = await Patient(); Assert.True((await Book(other, session.Id)).IsSuccess);
        var current = (await Schedule.Call(doctor, s => s.GetSessionAsync(session.Id, default))).Value;
        Assert.True((await Schedule.Edit(doctor, current, SessionEditKind.Capacity, value: 1)).IsError);
    }
    [SqlServerFact]
    public async Task DeliveryFailureNeverRollsBackTheBookingAndAdminCanRetryTheDurableNotice()
    {
        var (_, patient, session) = await Setup();
        var folder = Path.Combine(fixture.PrivateRoot, "development-booking-notices");
        // Use an isolated invalid path without disturbing files from earlier tests in this fixture.
        var held = folder + "-held-" + Guid.NewGuid().ToString("N");
        var ownedRoot = Path.GetFullPath(fixture.PrivateRoot) + Path.DirectorySeparatorChar;
        Assert.StartsWith(ownedRoot, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(ownedRoot, Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(folder)) Directory.Move(folder, held);
        AppointmentDto row;
        try
        {
            await File.WriteAllTextAsync(folder, "Synthetic delivery failure");
            row = (await Book(patient, session.Id)).Value.Appointment;
            Assert.Equal("Confirmed", row.Status);
        }
        finally
        {
            if (File.Exists(folder)) File.Delete(folder);
            if (Directory.Exists(held)) Directory.Move(held, folder);
        }
        fixture.Database.Clock.UtcNow = fixture.Database.Clock.GetUtcNow().AddMinutes(2);
        Assert.Equal(AppointmentErrors.Forbidden, Assert.Single((await Call(patient, s => s.RetryNoticesAsync(default))).Errors));
        var retry = (await Call(fixture.Admin.Id, s => s.RetryNoticesAsync(default))).Value;
        Assert.Equal(2, retry.DevelopmentFiles);
        await using var db = fixture.Database.CreateContext();
        Assert.All(await db.BookingNotices.Where(n => n.AppointmentId == row.Id).ToArrayAsync(), n => { Assert.Equal("DevelopmentFile", n.DeliveryStatus); Assert.Equal(2, n.Attempts); });
        Assert.Equal(0, (await Call(fixture.Admin.Id, s => s.RetryNoticesAsync(default))).Value.Attempted);
    }
    [SqlServerFact]
    public async Task SqlUniquenessAndCompositeOwnershipProtectAppointmentsOutsideTheHandler()
    {
        var (_, patient, session) = await Setup(); var booked = (await Book(patient, session.Id)).Value.Appointment;
        await using (var db = fixture.Database.CreateContext())
        {
            var existing = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == booked.Id);
            db.Appointments.Add(Appointment.Create(existing.PatientId, existing.PatientUserId, existing.DoctorUserId, existing.Selection).Value);
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.True(duplicate.InnerException is SqlException { Number: 2601 or 2627 });
        }
        await using var clean = fixture.Database.CreateContext(); var other = await fixture.UserAsync("Patient");
        var invalidFact = await Assert.ThrowsAsync<SqlException>(() => clean.Database.ExecuteSqlInterpolatedAsync($"UPDATE Appointments SET Status=4, CancelledByUserId={patient}, CancelledAtUtc={fixture.Database.Clock.GetUtcNow()}, CancellationReason='Missing prior state', CancellationCategory='PatientRequest', StatusBeforeCancellation=NULL WHERE Id={booked.Id}")); Assert.Equal(547, invalidFact.Number);
        var fk = await Assert.ThrowsAsync<SqlException>(() => clean.Database.ExecuteSqlInterpolatedAsync($"UPDATE Appointments SET PatientUserId={other.Id} WHERE Id={booked.Id}")); Assert.Equal(547, fk.Number);
        Assert.Equal(1, (await clean.BookingSessions.SingleAsync(s => s.Id == session.Id)).AllocatedCount);
    }
    [SqlServerFact]
    public async Task MigrationDownCannotDeleteRecordedAppointments()
    {
        var (_, patient, session) = await Setup(); var booked = (await Book(patient, session.Id)).Value.Appointment;
        await using var db = fixture.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        try
        {
            var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028161807_DoctorScheduling"));
            Assert.Equal(51015, failure.Number); Assert.True(await db.Appointments.AnyAsync(a => a.Id == booked.Id));
        }
        finally { await migrator.MigrateAsync(); }
    }
    [SqlServerTheory]
    [InlineData("capacity")]
    [InlineData("pause")]
    public async Task BookingAndDoctorControlsUseTheSameConsistencyProtocol(string control)
    {
        var (doctor, patient, session) = await Setup(2);
        await Book(patient, session.Id); var other = await Patient(); var input = Accept((await Preview(other, session.Id)).Value);
        var current = (await Schedule.Call(doctor, s => s.GetSessionAsync(session.Id, default))).Value;
        var booking = Call(other, s => s.BookAsync(input, default));
        var edit = Schedule.Edit(doctor, current, control == "capacity" ? SessionEditKind.Capacity : SessionEditKind.Pause, value: 1);
        await Task.WhenAll(booking, edit);
        Assert.True(booking.Result.IsSuccess || edit.Result.IsSuccess);
        await using var db = fixture.Database.CreateContext(); var saved = await db.BookingSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.InRange(saved.AllocatedCount, 1, saved.MaxCapacity);
        Assert.Equal(saved.AllocatedCount, await db.Appointments.CountAsync(a => a.SessionId == session.Id && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.Completed)));
        if (edit.Result.IsSuccess && control == "pause") Assert.True(saved.BookingPaused);
    }
    [SqlServerFact]
    public async Task ClaimFailureReportsUnavailableInsteadOfASuccessfulZeroAndTheCommittedBookingSurvives()
    {
        var (_, patient, session) = await Setup(); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_BookingTest_FailClaim ON dbo.BookingNotices AFTER UPDATE AS THROW 51072, 'Synthetic delivery claim failure.', 1;");
        AppointmentDto row;
        try
        {
            row = (await Book(patient, session.Id)).Value.Appointment;
            var failed = (await Call(fixture.Admin.Id, s => s.RetryNoticesAsync(default))).Value;
            Assert.Equal(2, failed.Attempted); Assert.Equal(2, failed.Unavailable); Assert.Equal(0, failed.DevelopmentFiles);
            Assert.Equal("Confirmed", (await Call(patient, s => s.GetAsync(row.Id, default))).Value.Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_BookingTest_FailClaim;"); }
        Assert.Equal(2, (await Call(fixture.Admin.Id, s => s.RetryNoticesAsync(default))).Value.DevelopmentFiles);
    }
}
