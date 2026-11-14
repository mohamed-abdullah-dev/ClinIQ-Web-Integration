using ClinIQ.Application.Features.Attendance;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common.Results;

namespace ClinIQ.Application.Features.AttendanceFeedback;

public sealed record FeedbackDto(Guid Id, Guid AppointmentId, Guid SourceAttendanceRecordId, int SourceRevision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, PatientAttendanceOutcome? Outcome, int ResponseRevision,
    DateTimeOffset? RespondedAtUtc, AttendanceReviewState? ReviewState, PatientAttendanceOutcome? Finding,
    DateTimeOffset? ReviewedAtUtc, bool Applicable, bool CanRespond, string Version);
public sealed record FeedbackAnswerInput(Guid SubmissionId, PatientAttendanceOutcome Outcome, string Version);
public sealed record FeedbackResponseDto(Guid Id, Guid SubmissionId, int Revision, Guid ActorUserId,
    PatientAttendanceOutcome Outcome, DateTimeOffset SubmittedAtUtc, bool IsEdit);
public sealed record FeedbackAnswerReceipt(FeedbackDto Current, FeedbackResponseDto Submitted, bool Replayed);
public sealed record FeedbackReviewInput(string Version, string AppointmentVersion, AttendanceReviewState State,
    PatientAttendanceOutcome? Finding, string Reason, DateTimeOffset? ArrivedAtUtc = null, DateTimeOffset? CompletedAtUtc = null);
public sealed record FeedbackReviewDetail(FeedbackDto Feedback, Guid PatientUserId, AppointmentSelection Booking,
    AttendanceFacts Attendance, string AppointmentVersion, Guid? ReviewedByUserId, string? ReviewReason,
    int? ReviewedResponseRevision, string? ReviewedAppointmentVersion);
public interface IAttendanceFeedbackService
{
    Task<Result<AttendancePage<FeedbackDto>>> ListAsync(int page, int size, CancellationToken ct);
    Task<Result<FeedbackDto>> GetAsync(Guid id, CancellationToken ct);
    Task<Result<FeedbackAnswerReceipt>> AnswerAsync(Guid id, FeedbackAnswerInput input, bool edit, CancellationToken ct);
    Task<Result<AttendancePage<FeedbackDto>>> ReviewsAsync(AttendanceReviewState state, int page, int size, CancellationToken ct);
    Task<Result<FeedbackReviewDetail>> ReviewAsync(Guid id, CancellationToken ct);
    Task<Result<AttendancePage<FeedbackResponseDto>>> ResponsesAsync(Guid id, int page, int size, bool admin, CancellationToken ct);
    Task<Result<AttendancePage<AttendanceHistory>>> StaffHistoryAsync(Guid id, int page, int size, CancellationToken ct);
    Task<Result<FeedbackReviewDetail>> FinalizeAsync(Guid id, FeedbackReviewInput input, CancellationToken ct);
}
