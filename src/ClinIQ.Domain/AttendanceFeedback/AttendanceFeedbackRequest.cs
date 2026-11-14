using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common;
using ClinIQ.Domain.Common.Results;

namespace ClinIQ.Domain.AttendanceFeedback;

public enum PatientAttendanceOutcome { AttendedAndSeen = 1, AttendedNotSeen = 2, DidNotAttend = 3 }
public enum AttendanceReviewState { Open = 1, Resolved = 2, Inconclusive = 3 }

/// <summary>A patient claim about one immutable staff NoShow event, separate from operational attendance.</summary>
public sealed class AttendanceFeedbackRequest : Entity
{
    public Guid AppointmentId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid PatientUserId { get; private set; }
    public Guid SourceAttendanceRecordId { get; private set; }
    public int SourceRevision { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public PatientAttendanceOutcome? Outcome { get; private set; }
    public int ResponseRevision { get; private set; }
    public DateTimeOffset? RespondedAtUtc { get; private set; }
    public AttendanceReviewState? ReviewState { get; private set; }
    public PatientAttendanceOutcome? Finding { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public DateTimeOffset? ReviewedAtUtc { get; private set; }
    public string? ReviewReason { get; private set; }
    public int? ReviewedResponseRevision { get; private set; }
    public byte[]? ReviewedAppointmentVersion { get; private set; }
    public byte[] Version { get; private set; } = [];
    public bool IsFinal => ReviewState is AttendanceReviewState.Resolved or AttendanceReviewState.Inconclusive;

    private AttendanceFeedbackRequest() { }
    private AttendanceFeedbackRequest(Guid id) : base(id) { }

    public static AttendanceFeedbackRequest Create(Appointment appointment, AttendanceRecord source)
    {
        if (source.AppointmentId != appointment.Id || source.PatientId != appointment.PatientId ||
            source.NewStatus != AppointmentStatus.NoShow || source.PreviousStatus == AppointmentStatus.NoShow ||
            appointment.Status != AppointmentStatus.NoShow || source.Revision != appointment.AttendanceRevision)
            throw new ArgumentException("Feedback requires the appointment's new immutable NoShow event.");
        return new(Guid.NewGuid())
        {
            AppointmentId = appointment.Id, PatientId = appointment.PatientId, PatientUserId = appointment.PatientUserId,
            SourceAttendanceRecordId = source.Id, SourceRevision = source.Revision,
            CreatedAtUtc = source.RecordedAtUtc, ExpiresAtUtc = source.RecordedAtUtc.AddDays(7)
        };
    }

    public bool IsApplicable(Appointment appointment) => appointment.Id == AppointmentId &&
        appointment.Status == AppointmentStatus.NoShow && appointment.AttendanceRevision == SourceRevision;

    public Result<int> Respond(PatientAttendanceOutcome outcome, bool edit, bool applicable, DateTimeOffset now)
    {
        if (!Enum.IsDefined(outcome)) return FeedbackErrors.Outcome;
        if (IsFinal) return FeedbackErrors.Final;
        if (!applicable) return FeedbackErrors.Inapplicable;
        if (now < CreatedAtUtc || now >= ExpiresAtUtc) return FeedbackErrors.Expired;
        if (edit != Outcome.HasValue) return FeedbackErrors.ExplicitEdit;
        Outcome = outcome; ResponseRevision = checked(ResponseRevision + 1); RespondedAtUtc = now;
        // Once opened, a case survives later agreement or staff correction until explicit Admin closure.
        if (outcome != PatientAttendanceOutcome.DidNotAttend) ReviewState ??= AttendanceReviewState.Open;
        return ResponseRevision;
    }

    public Result<int> FinalizeReview(AttendanceReviewState state, PatientAttendanceOutcome? finding,
        string reason, Guid reviewer, byte[] reviewedAppointmentVersion, DateTimeOffset now)
    {
        if (IsFinal) return FeedbackErrors.Final;
        if (ReviewState != AttendanceReviewState.Open || ResponseRevision == 0) return FeedbackErrors.NoReview;
        if (state is not (AttendanceReviewState.Resolved or AttendanceReviewState.Inconclusive) ||
            (state == AttendanceReviewState.Resolved ? !finding.HasValue || !Enum.IsDefined(finding.Value) : finding.HasValue))
            return FeedbackErrors.Finding;
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000) return FeedbackErrors.Reason;
        if (reviewer == Guid.Empty || reviewedAppointmentVersion.Length != 8 || now < RespondedAtUtc)
            throw new ArgumentException("A final decision requires its authenticated reviewer, current booking version and server time.");
        ReviewState = state; Finding = finding; ReviewReason = reason.Trim(); ReviewedByUserId = reviewer;
        ReviewedAtUtc = now; ReviewedResponseRevision = ResponseRevision; ReviewedAppointmentVersion = reviewedAppointmentVersion.ToArray();
        return ResponseRevision;
    }
}

public static class FeedbackErrors
{
    public static readonly Error Unauthorized = Error.Unauthorized("Feedback.Unauthorized", "Please log in to continue.");
    public static readonly Error Forbidden = Error.Forbidden("Feedback.Forbidden", "A confirmed Patient on Mobile or Admin on Web is required for this operation.");
    public static readonly Error NotFound = Error.NotFound("Feedback.NotFound", "The feedback request is unavailable.");
    public static readonly Error Outcome = Error.Validation("Feedback.Outcome", "Choose attended and seen, attended but not seen, or did not attend.");
    public static readonly Error Expired = Error.Conflict("Feedback.Expired", "The seven-day response window has ended.");
    public static readonly Error Final = Error.Conflict("Feedback.Final", "Admin has finalized this review; normal response edits are closed.");
    public static readonly Error Inapplicable = Error.Conflict("Feedback.Inapplicable", "Staff attendance has changed. Reload the current booking and feedback state.");
    public static readonly Error ExplicitEdit = Error.Conflict("Feedback.ExplicitEdit", "Use initial submission for the first answer and explicit edit for subsequent answers.");
    public static readonly Error Stale = Error.Conflict("Feedback.StaleVersion", "Reload the current feedback and appointment before retrying.");
    public static readonly Error ReplayMismatch = Error.Conflict("Feedback.RequestIdReused", "This submission ID was already used for a different request body.");
    public static readonly Error Busy = Error.Conflict("Feedback.Busy", "The booking is being updated. Retry shortly.");
    public static readonly Error NoReview = Error.Conflict("Feedback.NoReview", "This request has no open discrepancy review.");
    public static readonly Error Finding = Error.Validation("Feedback.Finding", "Resolved requires one finding; Inconclusive must have no finding.");
    public static readonly Error Reason = Error.Validation("Feedback.Reason", "Provide a review reason of 1 to 2000 characters.", "Reason");
    public static readonly Error Closed = Error.Conflict("Feedback.ClosedScope", "The booking's current operational scope does not permit an attendance correction.");
}
