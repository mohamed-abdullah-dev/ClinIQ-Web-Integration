using System.Text.Json;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.AttendanceFeedback;

public sealed class FeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private static (AttendanceFeedbackRequest Request, Appointment Appointment) Setup()
    {
        var a = Appointment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null,
            "Doctor", ProfessionalGrade.GeneralPractitioner, Guid.NewGuid(), "تخصص", "Specialty", "Place", "Address", new(2026, 11, 1), new(18, 0), new(20, 0),
            "Africa/Cairo", Now.AddHours(-2), Now, Now, 100, "EGP", "Patient", "01012345678")).Value;
        var input = new AttendanceInput(a.PatientId, AttendanceAction.NoShow, Convert.ToBase64String(new byte[8]));
        var change = a.RecordAttendance(input, a.DoctorUserId, true, false, Now, null).Value;
        var record = AttendanceRecord.Create(a, a.DoctorUserId, Now, input, change, JsonSerializer.Serialize(change.Before), JsonSerializer.Serialize(change.After), new byte[8]);
        return (AttendanceFeedbackRequest.Create(a, record), a);
    }
    [Fact]
    public void WindowIsSevenElapsedDaysFromTheSourceEventAndSilenceIsNotAnAnswer()
    {
        var (r, a) = Setup(); Assert.Equal(Now, r.CreatedAtUtc); Assert.Equal(Now.AddDays(7), r.ExpiresAtUtc);
        Assert.Null(r.Outcome); Assert.Null(r.ReviewState); Assert.Equal(0, r.ResponseRevision);
        Assert.Equal(AppointmentStatus.NoShow, a.Status); Assert.True(r.IsApplicable(a));
    }
    [Theory]
    [InlineData(PatientAttendanceOutcome.AttendedAndSeen, AttendanceReviewState.Open)]
    [InlineData(PatientAttendanceOutcome.AttendedNotSeen, AttendanceReviewState.Open)]
    [InlineData(PatientAttendanceOutcome.DidNotAttend, null)]
    public void ThreeAnswersAreClaimsAndOnlyAttendedClaimsOpenAReview(PatientAttendanceOutcome answer, AttendanceReviewState? review)
    {
        var (r, a) = Setup(); Assert.True(r.Respond(answer, false, true, Now).IsSuccess);
        Assert.Equal(answer, r.Outcome); Assert.Equal(review, r.ReviewState); Assert.Equal(AppointmentStatus.NoShow, a.Status);
    }
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void InitialAnswerHasAnExactExpiryBoundary(int ticksFromExpiry, bool allowed)
    {
        var (r, _) = Setup(); var result = r.Respond(PatientAttendanceOutcome.DidNotAttend, false, true, r.ExpiresAtUtc.AddTicks(ticksFromExpiry));
        Assert.Equal(allowed, result.IsSuccess);
    }
    [Fact]
    public void ExplicitEditsKeepTheDeadlineAndPreviouslyOpenedReview()
    {
        var (r, _) = Setup(); r.Respond(PatientAttendanceOutcome.AttendedNotSeen, false, true, Now);
        Assert.Equal(FeedbackErrors.ExplicitEdit, Assert.Single(r.Respond(PatientAttendanceOutcome.DidNotAttend, false, true, Now).Errors));
        Assert.True(r.Respond(PatientAttendanceOutcome.DidNotAttend, true, true, Now.AddDays(6)).IsSuccess);
        Assert.Equal(2, r.ResponseRevision); Assert.Equal(AttendanceReviewState.Open, r.ReviewState); Assert.Equal(Now.AddDays(7), r.ExpiresAtUtc);
        Assert.Equal(FeedbackErrors.Expired, Assert.Single(r.Respond(PatientAttendanceOutcome.AttendedAndSeen, true, true, r.ExpiresAtUtc).Errors));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void InvalidAnswerDoesNotMutateTheRequest(int answer)
    { var (r, _) = Setup(); Assert.True(r.Respond((PatientAttendanceOutcome)answer, false, true, Now).IsError); Assert.Null(r.Outcome); }
    [Fact]
    public void StaleStaffEventIsReadOnlyAndDoesNotReceiveANewDeadline()
    {
        var (r, a) = Setup(); a.RecordAttendance(new(a.PatientId, AttendanceAction.Correct, "unused", CorrectedStatus: AppointmentStatus.Confirmed, Reason: "Correction"), a.DoctorUserId, true, false, Now, null);
        Assert.False(r.IsApplicable(a)); Assert.Equal(FeedbackErrors.Inapplicable, Assert.Single(r.Respond(PatientAttendanceOutcome.AttendedAndSeen, false, false, Now).Errors));
        Assert.Equal(Now.AddDays(7), r.ExpiresAtUtc);
    }
    [Theory]
    [InlineData(AttendanceReviewState.Resolved, PatientAttendanceOutcome.DidNotAttend)]
    [InlineData(AttendanceReviewState.Inconclusive, null)]
    public void FinalReviewClosesNormalEditsAndCapturesReviewedVersions(AttendanceReviewState state, PatientAttendanceOutcome? finding)
    {
        var (r, _) = Setup(); r.Respond(PatientAttendanceOutcome.AttendedAndSeen, false, true, Now);
        var reviewer = Guid.NewGuid(); byte[] version = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.True(r.FinalizeReview(state, finding, "Contacted both sides", reviewer, version, Now.AddDays(8)).IsSuccess);
        Assert.True(r.IsFinal); Assert.Equal(reviewer, r.ReviewedByUserId); Assert.Equal(1, r.ReviewedResponseRevision); Assert.Equal(version, r.ReviewedAppointmentVersion);
        Assert.Equal(FeedbackErrors.Final, Assert.Single(r.Respond(PatientAttendanceOutcome.DidNotAttend, true, true, Now.AddDays(8)).Errors));
        Assert.Equal(FeedbackErrors.Final, Assert.Single(r.FinalizeReview(state, finding, "Overwrite", reviewer, version, Now.AddDays(9)).Errors));
    }
    [Fact]
    public void SilenceAndInitiallyMatchingAbsenceCannotBeFinalizedAsDisputes()
    {
        var (r, _) = Setup(); Assert.True(r.FinalizeReview(AttendanceReviewState.Inconclusive, null, "No answer", Guid.NewGuid(), new byte[8], Now).IsError);
        r.Respond(PatientAttendanceOutcome.DidNotAttend, false, true, Now);
        Assert.Equal(FeedbackErrors.NoReview, Assert.Single(r.FinalizeReview(AttendanceReviewState.Resolved, PatientAttendanceOutcome.DidNotAttend, "Matching", Guid.NewGuid(), new byte[8], Now).Errors));
    }
    [Theory]
    [InlineData(AttendanceReviewState.Open, null)]
    [InlineData(AttendanceReviewState.Resolved, null)]
    [InlineData(AttendanceReviewState.Inconclusive, PatientAttendanceOutcome.DidNotAttend)]
    public void InvalidReviewCombinationDoesNotCloseTheCase(AttendanceReviewState state, PatientAttendanceOutcome? finding)
    {
        var (r, _) = Setup(); r.Respond(PatientAttendanceOutcome.AttendedNotSeen, false, true, Now);
        Assert.True(r.FinalizeReview(state, finding, "Reason", Guid.NewGuid(), new byte[8], Now).IsError); Assert.False(r.IsFinal);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReviewNeedsAnAuditReason(string reason)
    {
        var (r, _) = Setup(); r.Respond(PatientAttendanceOutcome.AttendedNotSeen, false, true, Now);
        Assert.Equal(FeedbackErrors.Reason, Assert.Single(r.FinalizeReview(AttendanceReviewState.Inconclusive, null, reason, Guid.NewGuid(), new byte[8], Now).Errors));
    }
}
