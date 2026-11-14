using System.Text.Json;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AttendanceTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    internal sealed record Scenario(Guid Doctor, Guid Receptionist, SessionDto Session, AttendanceAppointment[] Rows, Guid[] Patients, Guid? BranchId, Guid? Manager);
    internal Task<T> Call<T>(Guid? actor, Func<IAttendanceService, Task<T>> run) => fixture.AsAsync(actor, p => run(p.GetRequiredService<IAttendanceService>()));
    internal async Task<Scenario> Setup(int count = 1, bool managed = false)
    {
        var schedule = new SchedulingTests(fixture); var (doctor, place) = await schedule.Place(); var location = place.Id; Guid? branchId = null; Guid? manager = null;
        var invitations = new WorkInvitationTests(fixture); await using var db = fixture.Database.CreateContext();
        if (managed)
        {
            var branch = await new PremisesCorrectionTests(fixture).Branch(); branchId = branch.Branch.Id; manager = branch.Owner;
            var email = await db.Users.Where(u => u.Id == doctor).Select(u => u.Email).SingleAsync();
            var invitation = (await invitations.Issue(manager.Value, WorkInvitationKind.Doctor, branchId, null, email!, 150)).Value;
            Assert.True((await invitations.Respond(doctor, invitation)).IsSuccess);
            location = await db.DoctorPracticeLocations.Where(p => p.UserId == doctor && p.ClinicBranchId == branchId).Select(p => p.Id).SingleAsync();
        }
        var session = (await schedule.Extra(doctor, location, schedule.Tomorrow, 18, 20, count + 2)).Value;
        var helper = new AppointmentTests(fixture); var patients = new List<Guid>();
        for (var i = 0; i < count; i++) { var patient = await helper.Patient(); patients.Add(patient); Assert.True((await helper.Book(patient, session.Id)).IsSuccess); }
        var staff = await fixture.UserAsync("Receptionist");
        var staffInvite = (await invitations.Issue(manager ?? doctor, WorkInvitationKind.Receptionist, branchId, branchId.HasValue ? null : location, staff.Email!)).Value;
        Assert.True((await invitations.Respond(staff.Id, staffInvite)).IsSuccess);
        var rows = (await Call(staff.Id, s => s.QueueAsync(session.DoctorProfileId, session.Id, 1, 100, default))).Value.Appointments.Items.ToArray();
        fixture.Database.Clock.UtcNow = session.StartsAtUtc;
        return new(doctor, staff.Id, session, rows, patients.ToArray(), branchId, manager);
    }
    internal Task<Result<AttendanceWriteResult>> Write(Guid actor, Scenario x, AttendanceAppointment row, AttendanceAction action,
        DateTimeOffset? arrived = null, DateTimeOffset? completed = null, AppointmentStatus? target = null, string? reason = null, string? version = null) =>
        Call(actor, s => s.WriteAsync(x.Session.DoctorProfileId, x.Session.Id, row.Id, new(row.PatientId, action, version ?? row.Version, arrived, completed, target, reason), default));
    internal async Task<AttendanceAppointment> Current(Guid actor, Scenario x, Guid id) => (await Call(actor, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 100, default))).Value.Appointments.Items.Single(a => a.Id == id);

    [SqlServerFact]
    public async Task RealArrivalOrderWinsOverBookingOrderAndRepeatedCheckInDoesNotMoveIt()
    {
        var x = await Setup(2); var first = x.Rows[0]; var second = x.Rows[1];
        var b = (await Write(x.Receptionist, x, second, AttendanceAction.CheckIn)).Value;
        var a = (await Write(x.Doctor, x, first, AttendanceAction.CheckIn)).Value;
        Assert.Equal(1, b.Appointment.Facts.QueueNumber); Assert.Equal(2, a.Appointment.Facts.QueueNumber);
        var repeat = (await Write(x.Receptionist, x, second, AttendanceAction.CheckIn)).Value;
        Assert.False(repeat.Changed); Assert.Equal(b.Appointment, repeat.Appointment);
        var queue = (await Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).Value;
        Assert.Equal(new[] { second.Id, first.Id }, queue.Appointments.Items.Select(r => r.Id)); Assert.Equal(2, queue.Session.LastQueueNumber); Assert.Equal(2, queue.Session.AllocatedCount);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(2, await db.AttendanceRecords.CountAsync(r => r.SessionId == x.Session.Id));
    }
    [SqlServerFact]
    public async Task ConcurrentDifferentPatientsGetDistinctMonotonicQueueNumbers()
    {
        var x = await Setup(4); var results = await Task.WhenAll(x.Rows.Select(r => Write(x.Receptionist, x, r, AttendanceAction.CheckIn)));
        Assert.All(results, r => Assert.True(r.IsSuccess)); Assert.Equal(new int?[] { 1, 2, 3, 4 }, results.Select(r => r.Value.Appointment.Facts.QueueNumber).Order());
        await using var db = fixture.Database.CreateContext(); var session = await db.BookingSessions.SingleAsync(s => s.Id == x.Session.Id); Assert.Equal(4, session.LastQueueNumber); Assert.Equal(4, session.AllocatedCount); Assert.Equal(x.Session.MaxCapacity, session.MaxCapacity);
    }
    [SqlServerFact]
    public async Task ConcurrentSameCheckInCreatesOneFactAndOneAuditRecord()
    {
        var x = await Setup(); var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)));
        Assert.All(results, r => Assert.True(r.IsSuccess)); Assert.Single(results, r => r.Value.Changed);
        Assert.All(results, r => Assert.Equal(1, r.Value.Appointment.Facts.QueueNumber));
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.AttendanceRecords.CountAsync(r => r.AppointmentId == x.Rows[0].Id));
    }
    [SqlServerFact]
    public async Task AssignedReceptionistCanCorrectCompletionDirectlyWithImmutableBeforeAfterFacts()
    {
        var x = await Setup(); var first = (await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        fixture.Database.Clock.UtcNow = x.Session.StartsAtUtc.AddMinutes(30);
        var completedAt = x.Session.StartsAtUtc.AddMinutes(20);
        var complete = (await Write(x.Receptionist, x, first, AttendanceAction.Complete, completed: completedAt)).Value.Appointment;
        var corrected = (await Write(x.Receptionist, x, complete, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Mistakenly completed while still waiting")).Value.Appointment;
        Assert.Equal(AppointmentStatus.CheckedIn, corrected.Facts.Status); Assert.Equal(first.Facts.ArrivedAtUtc, corrected.Facts.ArrivedAtUtc); Assert.Equal(first.Facts.QueueNumber, corrected.Facts.QueueNumber); Assert.Null(corrected.Facts.CompletedAtUtc);
        var history = (await Call(x.Receptionist, s => s.HistoryAsync(x.Session.DoctorProfileId, x.Session.Id, first.Id, 1, 20, default))).Value;
        Assert.Equal(new[] { 1, 2, 3 }, history.Items.Select(r => r.Revision)); Assert.Equal(completedAt, history.Items[2].Before.CompletedAtUtc); Assert.Null(history.Items[2].After.CompletedAtUtc); Assert.Equal(x.Receptionist, history.Items[2].ActorUserId);
        Assert.Equal(fixture.Database.Clock.GetUtcNow(), history.Items[1].RecordedAtUtc); Assert.Equal(history.Items[1].RecordedAtUtc, history.Items[2].RecordedAtUtc);
        await using var db = fixture.Database.CreateContext(); var a = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == first.Id); Assert.Equal(100, a.Fee); Assert.Equal(x.Doctor, a.DoctorUserId); Assert.Equal(x.Patients[0], a.PatientUserId); Assert.Equal(1, (await db.BookingSessions.SingleAsync(s => s.Id == x.Session.Id)).AllocatedCount);
    }
    [SqlServerFact]
    public async Task CompletionHasFactualTimeSeparateFromDelayedRecordingAndAppearsOnlyOnOwnPatientBooking()
    {
        var x = await Setup(); var row = (await Write(x.Doctor, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        var factual = x.Session.EndsAtUtc.AddMinutes(30); fixture.Database.Clock.UtcNow = x.Session.EndsAtUtc.AddDays(2);
        var complete = (await Write(x.Receptionist, x, row, AttendanceAction.Complete, completed: factual)).Value.Appointment;
        Assert.Equal(factual, complete.Facts.CompletedAtUtc); Assert.Equal(fixture.Database.Clock.GetUtcNow(), complete.Facts.CompletionRecordedAtUtc);
        var own = (await new AppointmentTests(fixture).Call(x.Patients[0], s => s.GetAsync(row.Id, default))).Value;
        Assert.Equal(factual, own.CompletedAtUtc); Assert.Equal(1, own.QueueNumber);
        Assert.Equal(AttendanceErrors.State, Assert.Single((await Write(x.Receptionist, x, complete, AttendanceAction.NoShow)).Errors));
    }
    [SqlServerFact]
    public async Task EarlyArrivalAndPrematureNoShowAreRejectedAndArrivalDoesNotRequireNewBookingAvailability()
    {
        var x = await Setup(); fixture.Database.Clock.UtcNow = x.Session.StartsAtUtc.AddSeconds(-1);
        Assert.Equal(AttendanceErrors.NotStarted, Assert.Single((await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).Errors));
        fixture.Database.Clock.UtcNow = x.Session.StartsAtUtc;
        Assert.Equal(AttendanceErrors.NotEnded, Assert.Single((await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.NoShow)).Errors));
        var schedule = new SchedulingTests(fixture); var current = (await schedule.Call(x.Doctor, s => s.GetSessionAsync(x.Session.Id, default))).Value;
        await schedule.Edit(x.Doctor, current, ClinIQ.Application.Features.Scheduling.SessionEditKind.Pause);
        Assert.True((await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).IsSuccess);
    }
    [SqlServerFact]
    public async Task LateCheckInRequiresTheResponsibleDoctorsExplicitActionAndPreservesTheAbsenceRecord()
    {
        var x = await Setup(); fixture.Database.Clock.UtcNow = x.Session.EndsAtUtc;
        var absent = (await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.NoShow)).Value.Appointment;
        fixture.Database.Clock.UtcNow = x.Session.EndsAtUtc.AddMinutes(5); var actual = x.Session.EndsAtUtc.AddMinutes(2);
        Assert.Equal(AttendanceErrors.Late, Assert.Single((await Write(x.Receptionist, x, absent, AttendanceAction.Correct, arrived: actual, target: AppointmentStatus.CheckedIn, reason: "New late arrival is not a correction")).Errors));
        Assert.Equal(AttendanceErrors.DoctorRequired, Assert.Single((await Write(x.Receptionist, x, absent, AttendanceAction.LateCheckIn, arrived: actual, reason: "Cannot self-authorize")).Errors));
        var late = (await Write(x.Doctor, x, absent, AttendanceAction.LateCheckIn, arrived: actual, reason: "Doctor explicitly accepts this late arrival")).Value.Appointment;
        Assert.Equal(x.Doctor, late.Facts.LateAuthorizedByUserId); Assert.Equal(actual, late.Facts.ArrivedAtUtc); Assert.Equal(fixture.Database.Clock.GetUtcNow(), late.Facts.CheckInRecordedAtUtc);
        var history = (await Call(x.Doctor, s => s.HistoryAsync(x.Session.DoctorProfileId, x.Session.Id, late.Id, 1, 20, default))).Value;
        Assert.Equal(new[] { "NoShow", "LateCheckIn" }, history.Items.Select(e => e.Action));
        var impostor = await fixture.UserAsync("Doctor"); await using var db = fixture.Database.CreateContext();
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Appointments SET LateAuthorizedByUserId={impostor.Id} WHERE Id={late.Id}"))).Number);
    }
    [SqlServerFact]
    public async Task CorrectingAMissedHistoricalArrivalAppendsAQueuePositionWithoutMovingEarlierPatients()
    {
        var x = await Setup(2); var first = (await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        fixture.Database.Clock.UtcNow = x.Session.EndsAtUtc;
        var absent = (await Write(x.Receptionist, x, x.Rows[1], AttendanceAction.NoShow)).Value.Appointment;
        var corrected = (await Write(x.Receptionist, x, absent, AttendanceAction.Correct, arrived: x.Session.StartsAtUtc, target: AppointmentStatus.CheckedIn, reason: "Actually arrived during the session; record was missed")).Value.Appointment;
        Assert.Equal(1, first.Facts.QueueNumber); Assert.Equal(2, corrected.Facts.QueueNumber); Assert.Equal(x.Session.StartsAtUtc, corrected.Facts.ArrivedAtUtc); Assert.Equal(x.Session.EndsAtUtc, corrected.Facts.CheckInRecordedAtUtc);
        Assert.Equal(2, (await Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).Value.Session.AllocatedCount);
    }
    [SqlServerFact]
    public async Task AttendedButNotSeenRetainsItsAllocationAndCannotBeRebookedIntoAnotherSlot()
    {
        var x = await Setup(); var row = (await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        var notSeen = (await Write(x.Receptionist, x, row, AttendanceAction.NotSeen, reason: "Left without consultation")).Value.Appointment;
        Assert.Equal(AppointmentStatus.AttendedNotSeen, notSeen.Facts.Status); Assert.Equal(row.Facts.ArrivedAtUtc, notSeen.Facts.ArrivedAtUtc);
        Assert.Equal(AppointmentErrors.Duplicate, Assert.Single((await new AppointmentTests(fixture).Book(x.Patients[0], x.Session.Id)).Errors));
        var correction = (await Write(x.Receptionist, x, notSeen, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Outcome was entered mistakenly; patient remains waiting")).Value.Appointment;
        Assert.Equal(row.Facts.QueueNumber, correction.Facts.QueueNumber);
    }
    [SqlServerTheory]
    [InlineData("Admin", true)]
    [InlineData("ClinicManager", true)]
    [InlineData("Patient", true)]
    [InlineData("Doctor", false)]
    [InlineData("Receptionist", false)]
    public async Task RolesAloneDoNotGrantOperationalPatientAccess(string role, bool forbidden)
    {
        var x = await Setup(); var user = await fixture.UserAsync(role);
        var result = await Call(user.Id, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default));
        Assert.Equal(forbidden ? AttendanceErrors.Forbidden : AttendanceErrors.NotFound, Assert.Single(result.Errors));
        Assert.Equal(AttendanceErrors.Unauthorized, Assert.Single((await Call(null, s => s.DoctorsAsync(default))).Errors));
    }
    [SqlServerFact]
    public async Task SelectedDoctorSessionPatientAndVersionAreValidatedBeforeMutation()
    {
        var x = await Setup(); var row = x.Rows[0];
        Assert.Equal(AttendanceErrors.NotFound, Assert.Single((await Call(x.Receptionist, s => s.QueueAsync(Guid.NewGuid(), x.Session.Id, 1, 20, default))).Errors));
        Assert.Equal(AttendanceErrors.PatientConfirmation, Assert.Single((await Call(x.Receptionist, s => s.WriteAsync(x.Session.DoctorProfileId, x.Session.Id, row.Id, new(Guid.NewGuid(), AttendanceAction.CheckIn, row.Version), default))).Errors));
        Assert.Equal(AttendanceErrors.Stale, Assert.Single((await Write(x.Receptionist, x, row, AttendanceAction.CheckIn, version: Convert.ToBase64String(new byte[8]))).Errors));
        await using var db = fixture.Database.CreateContext(); Assert.False(await db.AttendanceRecords.AnyAsync(r => r.SessionId == x.Session.Id));
    }
    [SqlServerFact]
    public async Task RevokingAnAssignmentStopsSubsequentQueueHistoryAndWriteRequests()
    {
        var x = await Setup(); var row = (await Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        var invitations = new WorkInvitationTests(fixture);
        var assignment = Assert.Single((await invitations.Workforce(x.Receptionist, s => s.ListAssignmentsAsync(default))).Value);
        Assert.True((await invitations.Workforce(x.Doctor, s => s.RevokeAssignmentAsync(assignment.Id, assignment.Version, "Assignment ended", default))).IsSuccess);
        Assert.Empty((await Call(x.Receptionist, s => s.DoctorsAsync(default))).Value);
        Assert.Equal(AttendanceErrors.NotFound, Assert.Single((await Call(x.Receptionist, s => s.HistoryAsync(x.Session.DoctorProfileId, x.Session.Id, row.Id, 1, 20, default))).Errors));
        Assert.Equal(AttendanceErrors.NotFound, Assert.Single((await Write(x.Receptionist, x, row, AttendanceAction.Complete, completed: fixture.Database.Clock.GetUtcNow())).Errors));
        Assert.True((await Write(x.Doctor, x, row, AttendanceAction.Complete, completed: fixture.Database.Clock.GetUtcNow())).IsSuccess);
    }
    [SqlServerFact]
    public async Task BranchAssignmentCoversItsDoctorSessionsButCannotReadTheSameDoctorsIndependentPatients()
    {
        var x = await Setup(managed: true); Assert.NotNull(x.BranchId);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(x.BranchId, (await db.BookingSessions.SingleAsync(s => s.Id == x.Session.Id)).OperationalBranchId);
        var independent = await db.DoctorPracticeLocations.Where(p => p.UserId == x.Doctor && p.ClinicBranchId == null).Select(p => p.Id).SingleAsync();
        fixture.Database.Clock.UtcNow = SqlServerIntegrationFixture.InitialUtc;
        var schedule = new SchedulingTests(fixture); var other = (await schedule.Extra(x.Doctor, independent, schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        Assert.Equal(AttendanceErrors.NotFound, Assert.Single((await Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, other.Id, 1, 20, default))).Errors));
        Assert.Single((await Call(x.Receptionist, s => s.DoctorsAsync(default))).Value);
        // Simulate a retained pre-link independent operational scope. Current branch ownership cannot import it.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE BookingSessions SET OperationalBranchId=NULL WHERE Id={x.Session.Id}");
        Assert.Equal(AttendanceErrors.NotFound, Assert.Single((await Call(x.Receptionist, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).Errors));
        Assert.Single((await Call(x.Doctor, s => s.QueueAsync(x.Session.DoctorProfileId, x.Session.Id, 1, 20, default))).Value.Appointments.Items);
    }
    [SqlServerFact]
    public async Task AttendanceHistoryIsSqlAppendOnlyAndItsScopeCannotBeForged()
    {
        var x = await Setup(); await Write(x.Doctor, x, x.Rows[0], AttendanceAction.CheckIn);
        await using var db = fixture.Database.CreateContext(); var id = await db.AttendanceRecords.Where(r => r.AppointmentId == x.Rows[0].Id).Select(r => r.Id).SingleAsync();
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AttendanceRecords SET Reason='Rewrite' WHERE Id={id}"))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AttendanceRecords WHERE Id={id}"))).Number);
        Assert.Equal(1, await db.AttendanceRecords.CountAsync(r => r.Id == id));
        var migrator = db.GetService<IMigrator>();
        try { Assert.Equal(51015, (await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261028173849_PatientAppointments"))).Number); }
        finally { await migrator.MigrateAsync(); }
    }
    [SqlServerFact]
    public async Task FailedAuditInsertRollsBackArrivalRevisionAndSessionQueueCounter()
    {
        var x = await Setup(); await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_AttendanceTest_FailInsert ON dbo.AttendanceRecords AFTER INSERT AS THROW 51079, 'Synthetic audit insertion failure.', 1;");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_AttendanceTest_FailInsert;"); }
        var row = await Current(x.Doctor, x, x.Rows[0].Id); Assert.Equal(AppointmentStatus.Confirmed, row.Facts.Status); Assert.Null(row.Facts.QueueNumber);
        var session = await db.BookingSessions.AsNoTracking().SingleAsync(s => s.Id == x.Session.Id); Assert.Equal(0, session.LastQueueNumber); Assert.Equal(1, session.AllocatedCount);
        Assert.Equal(0, (await db.Appointments.SingleAsync(a => a.Id == row.Id)).AttendanceRevision);
    }
    [SqlServerFact]
    public async Task CancellationAndCheckInRaceCannotResurrectACancelledBookingOrDoubleReleaseCapacity()
    {
        var x = await Setup(); var check = Write(x.Receptionist, x, x.Rows[0], AttendanceAction.CheckIn);
        var cancel = new AppointmentTests(fixture).Call(x.Patients[0], s => s.CancelAsync(x.Rows[0].Id, Guid.NewGuid(), "Cannot attend", default));
        await Task.WhenAll(check, cancel); Assert.True(check.Result.IsSuccess || cancel.Result.IsSuccess);
        var row = await Current(x.Doctor, x, x.Rows[0].Id); await using var db = fixture.Database.CreateContext(); var session = await db.BookingSessions.SingleAsync(s => s.Id == x.Session.Id);
        if (row.Facts.Status == AppointmentStatus.Cancelled) { Assert.Null(row.Facts.QueueNumber); Assert.Equal(0, session.AllocatedCount); Assert.Equal(0, session.LastQueueNumber); }
        else { Assert.Equal(AppointmentStatus.CheckedIn, row.Facts.Status); Assert.Equal(1, row.Facts.QueueNumber); Assert.Equal(1, session.AllocatedCount); }
    }
    [SqlServerFact]
    public async Task CancelledSessionCannotBeSilentlyReopenedByCorrectingACompletedVisitToWaiting()
    {
        var x = await Setup(); var row = (await Write(x.Doctor, x, x.Rows[0], AttendanceAction.CheckIn)).Value.Appointment;
        row = (await Write(x.Doctor, x, row, AttendanceAction.Complete, completed: fixture.Database.Clock.GetUtcNow())).Value.Appointment;
        var helper = new SchedulingTests(fixture); var session = (await helper.Call(x.Doctor, s => s.GetSessionAsync(x.Session.Id, default))).Value;
        Assert.True((await new AppointmentTests(fixture).Call(x.Doctor, s => s.CancelSessionAsync(session.Id, session.Version, "Doctor disruption", default))).IsSuccess);
        Assert.Equal(AttendanceErrors.Closed, Assert.Single((await Write(x.Receptionist, x, row, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Must not reopen outstanding visits")).Errors));
        Assert.Equal(AppointmentStatus.Completed, (await Current(x.Doctor, x, row.Id)).Facts.Status);
    }
}
