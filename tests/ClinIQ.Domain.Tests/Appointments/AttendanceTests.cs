using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Appointments;

public sealed class AttendanceTests
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(2);
    private static Appointment Row() => Appointment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null,
        "Doctor", ProfessionalGrade.GeneralPractitioner, Guid.NewGuid(), "تخصص", "Specialty", "Place", "Address", new(2026, 11, 1), new(18, 0), new(20, 0), "Africa/Cairo", Start, End, End, 100, "EGP", "Patient", "01012345678")).Value;
    private static AttendanceInput Input(Appointment a, AttendanceAction action, DateTimeOffset? arrival = null, DateTimeOffset? completed = null, AppointmentStatus? target = null, string? reason = null) =>
        new(a.PatientId, action, Convert.ToBase64String(new byte[8]), arrival, completed, target, reason);
    [Fact]
    public void OrdinaryArrivalStartsAtTheWindowAndRepeatsKeepTheFirstFactsAndQueue()
    {
        var a = Row(); var input = Input(a, AttendanceAction.CheckIn);
        Assert.Equal(AttendanceErrors.NotStarted, Assert.Single(a.RecordAttendance(input, a.DoctorUserId, true, false, Start.AddSeconds(-1), 1).Errors));
        var first = a.RecordAttendance(input, a.DoctorUserId, true, false, Start, 7).Value;
        Assert.True(first.Changed); Assert.Equal(7, a.QueueNumber); Assert.Equal(Start, a.ArrivedAtUtc);
        var repeated = a.RecordAttendance(input, a.DoctorUserId, true, false, End.AddHours(1), 8).Value;
        Assert.False(repeated.Changed); Assert.Equal(first.After, repeated.After); Assert.True(a.ConsumesCapacity);
    }
    [Fact]
    public void CompletionRequiresArrivalAndARealNonFutureConsultationTime()
    {
        var a = Row(); var input = Input(a, AttendanceAction.Complete, completed: Start.AddMinutes(10));
        Assert.Equal(AttendanceErrors.State, Assert.Single(a.RecordAttendance(input, a.DoctorUserId, true, false, Start.AddMinutes(11), 1).Errors));
        a.RecordAttendance(Input(a, AttendanceAction.CheckIn), a.DoctorUserId, true, false, Start.AddMinutes(5), 1);
        foreach (var invalid in new DateTimeOffset?[] { null, Start.AddMinutes(4), End.AddDays(4) })
            Assert.Equal(AttendanceErrors.Facts, Assert.Single(a.RecordAttendance(input with { CompletedAtUtc = invalid }, a.DoctorUserId, true, false, End.AddDays(3), 1).Errors));
        var actual = End.AddMinutes(20); var recorded = End.AddDays(3);
        Assert.True(a.RecordAttendance(input with { CompletedAtUtc = actual }, a.DoctorUserId, true, false, recorded, 1).IsSuccess);
        Assert.Equal(actual, a.CompletedAtUtc); Assert.Equal(recorded, a.CompletionRecordedAtUtc); Assert.True(a.ConsumesCapacity);
    }
    [Fact]
    public void CorrectingFalseCompletionRestoresWaitingWithoutChangingArrivalOrAllocatingAgain()
    {
        var a = Row(); a.RecordAttendance(Input(a, AttendanceAction.CheckIn), a.DoctorUserId, true, false, Start, 3);
        a.RecordAttendance(Input(a, AttendanceAction.Complete, completed: Start.AddMinutes(5)), a.DoctorUserId, true, false, Start.AddMinutes(10), 3);
        var result = a.RecordAttendance(Input(a, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn, reason: "Still waiting"), Guid.NewGuid(), false, false, Start.AddMinutes(20), 99).Value;
        Assert.Equal(Start.AddMinutes(5), result.Before.CompletedAtUtc); Assert.Null(a.CompletedAtUtc); Assert.Null(a.CompletionRecordedAtUtc);
        Assert.Equal(3, a.QueueNumber); Assert.Equal(Start, a.ArrivedAtUtc); Assert.True(a.ConsumesCapacity);
        Assert.Equal(AttendanceErrors.PreserveArrival, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.Correct, arrival: Start.AddMinutes(1), target: AppointmentStatus.CheckedIn, reason: "Change arrival"), a.DoctorUserId, true, false, End, 3).Errors));
    }
    [Fact]
    public void NoShowIsNotAllowedBeforeEndOrForAGenuinelyCheckedInPatient()
    {
        var a = Row(); Assert.Equal(AttendanceErrors.NotEnded, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.NoShow), a.DoctorUserId, true, false, End.AddSeconds(-1), 1).Errors));
        a.RecordAttendance(Input(a, AttendanceAction.CheckIn), a.DoctorUserId, true, false, Start, 1);
        Assert.Equal(AttendanceErrors.State, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.NoShow), a.DoctorUserId, true, false, End, 1).Errors));
        var neverArrived = Row(); Assert.True(neverArrived.RecordAttendance(Input(neverArrived, AttendanceAction.NoShow), neverArrived.DoctorUserId, true, false, End, 1).IsSuccess);
        Assert.True(neverArrived.ConsumesCapacity); Assert.Null(neverArrived.QueueNumber);
    }
    [Fact]
    public void GenuineLateArrivalRequiresTheResponsibleDoctorAndNeverBackdatesTheRecord()
    {
        var a = Row(); var now = End.AddMinutes(5);
        Assert.Equal(AttendanceErrors.Late, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.CheckIn), a.DoctorUserId, true, false, now, 1).Errors));
        var input = Input(a, AttendanceAction.LateCheckIn, arrival: End.AddMinutes(1), reason: "Doctor authorized late arrival");
        Assert.Equal(AttendanceErrors.DoctorRequired, Assert.Single(a.RecordAttendance(input, Guid.NewGuid(), false, false, now, 1).Errors));
        Assert.Equal(AttendanceErrors.DoctorRequired, Assert.Single(a.RecordAttendance(input, Guid.NewGuid(), true, false, now, 1).Errors));
        Assert.True(a.RecordAttendance(input, a.DoctorUserId, true, false, now, 1).IsSuccess);
        Assert.Equal(End.AddMinutes(1), a.ArrivedAtUtc); Assert.Equal(now, a.CheckInRecordedAtUtc); Assert.Equal(a.DoctorUserId, a.LateAuthorizedByUserId); Assert.Equal(now, a.LateAuthorizedAtUtc);
    }
    [Fact]
    public void MissedInWindowFactsCanBeCorrectedButANewLateArrivalCannotUseCorrection()
    {
        var a = Row(); a.RecordAttendance(Input(a, AttendanceAction.NoShow), a.DoctorUserId, true, false, End, 1);
        var correction = Input(a, AttendanceAction.Correct, arrival: End.AddMinutes(1), target: AppointmentStatus.CheckedIn, reason: "Late arrival");
        Assert.Equal(AttendanceErrors.Late, Assert.Single(a.RecordAttendance(correction, a.DoctorUserId, true, false, End.AddHours(1), 1).Errors));
        Assert.True(a.RecordAttendance(correction with { ArrivedAtUtc = Start.AddMinutes(10) }, Guid.NewGuid(), false, false, End.AddHours(1), 1).IsSuccess);
        Assert.Equal(Start.AddMinutes(10), a.ArrivedAtUtc); Assert.Equal(End.AddHours(1), a.CheckInRecordedAtUtc); Assert.Equal(1, a.QueueNumber);
    }
    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void CorrectionsCannotResurrectTerminalBookingOutcomes(AppointmentStatus state)
    {
        var a = Row(); if (state == AppointmentStatus.Cancelled) a.CancelByPatient(a.PatientUserId, "Cancelled", Start); else a.Reschedule(a.PatientUserId, Start);
        Assert.Equal(AttendanceErrors.State, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.Correct, arrival: Start, target: AppointmentStatus.CheckedIn, reason: "Cannot resurrect"), a.DoctorUserId, true, false, End, 1).Errors));
    }
    [Fact]
    public void PatientConfirmationReasonAndClosedSessionAreValidatedWithoutPartialMutation()
    {
        var a = Row(); var input = Input(a, AttendanceAction.CheckIn);
        Assert.Equal(AttendanceErrors.PatientConfirmation, Assert.Single(a.RecordAttendance(input with { ConfirmedPatientId = Guid.NewGuid() }, a.DoctorUserId, true, false, Start, 1).Errors));
        Assert.Equal(AttendanceErrors.Closed, Assert.Single(a.RecordAttendance(input, a.DoctorUserId, true, true, Start, 1).Errors));
        Assert.Equal(AppointmentStatus.Confirmed, a.Status); Assert.Null(a.QueueNumber);
        a.RecordAttendance(input, a.DoctorUserId, true, false, Start, 1);
        Assert.Equal(AttendanceErrors.Reason, Assert.Single(a.RecordAttendance(Input(a, AttendanceAction.NotSeen), a.DoctorUserId, true, false, Start, 1).Errors));
        Assert.True(a.RecordAttendance(Input(a, AttendanceAction.NotSeen, reason: "Left before consultation"), a.DoctorUserId, true, false, Start, 1).IsSuccess); Assert.True(a.ConsumesCapacity);
    }
}
