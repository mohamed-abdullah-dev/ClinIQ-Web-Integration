using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.VisitReviews;
using Xunit;

namespace ClinIQ.Domain.Tests.VisitReviews;

public sealed class VisitReviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
    private static Appointment Booking() => Appointment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "Doctor", ProfessionalGrade.GeneralPractitioner,
            Guid.NewGuid(), "تخصص", "Specialty", "Place", "Address", new(2026, 11, 1), new(18, 0), new(20, 0),
            "Africa/Cairo", Start, Start.AddHours(2), Start.AddHours(2), 100, "EGP", "Patient", "01012345678")).Value;
    private static void Attendance(Appointment a, AttendanceAction action, DateTimeOffset? completed = null,
        AppointmentStatus? target = null, DateTimeOffset? recorded = null) => Assert.True(a.RecordAttendance(
        new(a.PatientId, action, Convert.ToBase64String(new byte[8]), CompletedAtUtc: completed,
            CorrectedStatus: target, Reason: "Synthetic correction"), a.DoctorUserId, true, false, recorded ?? Start.AddHours(3), 1).IsSuccess);
    private static Appointment Completed(DateTimeOffset? recorded = null)
    {
        var a = Booking(); Attendance(a, AttendanceAction.CheckIn, recorded: Start);
        Attendance(a, AttendanceAction.Complete, Start.AddMinutes(10), recorded: recorded); return a;
    }

    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(-1)]
    public void InvalidStarsAreRejected(int stars) => Assert.Equal(VisitReviewErrors.Input,
        Assert.Single(VisitReview.Create(Completed(), stars, null, Start.AddHours(4)).Errors));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void AllApprovedStarsAreImmediatelyVisibleAndContextComesFromBooking(int stars)
    {
        var a = Completed(); var r = VisitReview.Create(a, stars, "  مراجعة  ", Start.AddHours(4)).Value;
        Assert.False(r.Hidden); Assert.Equal("مراجعة", r.Text); Assert.Equal(a.Id, r.AppointmentId);
        Assert.Equal(a.PatientUserId, r.PatientUserId); Assert.Equal(a.PatientId, r.PatientId);
        Assert.Equal(a.DoctorProfileId, r.DoctorProfileId); Assert.Equal(a.LocationId, r.LocationId);
        Assert.Equal(a.PlaceName, r.LocationName); Assert.Equal(1, r.Revision);
    }

    [Theory]
    [InlineData(AppointmentStatus.Confirmed)] [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.NoShow)] [InlineData(AppointmentStatus.AttendedNotSeen)]
    [InlineData(AppointmentStatus.Cancelled)] [InlineData(AppointmentStatus.Rescheduled)]
    public void EveryNonCompletedOutcomeIsIneligible(AppointmentStatus status)
    {
        var a = Booking();
        if (status is AppointmentStatus.CheckedIn or AppointmentStatus.AttendedNotSeen) Attendance(a, AttendanceAction.CheckIn, recorded: Start);
        if (status == AppointmentStatus.AttendedNotSeen) Attendance(a, AttendanceAction.NotSeen);
        if (status == AppointmentStatus.NoShow) Attendance(a, AttendanceAction.NoShow);
        if (status == AppointmentStatus.Cancelled) Assert.True(a.CancelByPatient(a.PatientUserId, "Synthetic cancellation", Start).IsSuccess);
        if (status == AppointmentStatus.Rescheduled) Assert.True(a.Reschedule(a.PatientUserId, Start).IsSuccess);
        Assert.Equal(status, a.Status);
        Assert.Equal(VisitReviewErrors.Ineligible, Assert.Single(VisitReview.Create(a, 5, null, Start.AddHours(4)).Errors));
    }

    [Fact]
    public void CreateAndEditUseFactualThirtyDaysEvenWhenCompletionIsRecordedLate()
    {
        var a = Completed(Start.AddDays(29)); var deadline = a.CompletedAtUtc!.Value.AddDays(30);
        var r = VisitReview.Create(a, 1, null, deadline.AddTicks(-1)).Value;
        Assert.True(r.Edit(a, 2, "Changed", deadline.AddTicks(-1)).IsSuccess);
        Assert.Equal(VisitReviewErrors.Expired, Assert.Single(r.Edit(a, 3, null, deadline).Errors));
        Assert.Equal(VisitReviewErrors.Expired, Assert.Single(VisitReview.Create(a, 5, null, deadline).Errors));
        Assert.Equal(VisitReviewErrors.Expired, Assert.Single(VisitReview.Create(a, 5, null, Start).Errors));
    }

    [Fact]
    public void HiddenReviewEditsNeverRestorePublicationAndInvalidWritesLeaveFactsUnchanged()
    {
        var a = Completed(); var r = VisitReview.Create(a, 1, null, Start.AddHours(4)).Value;
        Assert.Null(r.Text); Assert.True(r.Moderate(true, ReviewModerationCategory.PrivateInformation, "Private data", Guid.NewGuid(), Start.AddHours(5)).Value);
        Assert.True(r.Edit(a, 4, "Corrected", Start.AddHours(6)).Value); Assert.True(r.Hidden); Assert.Equal(3, r.Revision);
        Assert.False(r.Edit(a, 4, " Corrected ", Start.AddHours(6)).Value); Assert.Equal(3, r.Revision);
        Assert.True(r.Edit(a, 0, "Invalid", Start.AddHours(6)).IsError); Assert.Equal("Corrected", r.Text);
        Assert.True(r.Edit(a, 4, new string('ع', 2001), Start.AddHours(6)).IsError);
        Assert.True(r.Moderate(false, ReviewModerationCategory.Irrelevant, "Restore", Guid.NewGuid(), Start.AddHours(6)).IsError);
        Assert.True(r.Moderate(true, (ReviewModerationCategory)99, "Reason", Guid.NewGuid(), Start.AddHours(6)).IsError);
        Assert.True(r.Moderate(true, ReviewModerationCategory.Abusive, "  ", Guid.NewGuid(), Start.AddHours(6)).IsError);
        Assert.True(r.Moderate(false, null, "Private data removed", Guid.NewGuid(), Start.AddHours(7)).Value); Assert.False(r.Hidden);
    }

    [Fact]
    public void CorrectingCompletionAwayRetainsReviewAndPreventsEditing()
    {
        var a = Completed(); var r = VisitReview.Create(a, 2, "Preserved", Start.AddHours(4)).Value;
        Attendance(a, AttendanceAction.Correct, target: AppointmentStatus.CheckedIn);
        Assert.Equal(VisitReviewErrors.Ineligible, VisitReview.Eligibility(a, Start.AddHours(5)));
        Assert.True(r.Edit(a, 5, null, Start.AddHours(5)).IsError); Assert.Equal("Preserved", r.Text); Assert.Equal(1, r.Revision);
    }
}
