using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.AttendanceFeedback;

/// <summary>Append-only answer history and idempotency receipt; the request retains its latest answer.</summary>
public sealed class AttendanceFeedbackResponse : Entity
{
    public Guid FeedbackRequestId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public Guid SubmissionId { get; private set; }
    public int Revision { get; private set; }
    public PatientAttendanceOutcome Outcome { get; private set; }
    public DateTimeOffset SubmittedAtUtc { get; private set; }
    public bool IsEdit { get; private set; }
    public byte[] PreviousVersion { get; private set; } = [];
    private AttendanceFeedbackResponse() { }
    private AttendanceFeedbackResponse(Guid id) : base(id) { }
    public static AttendanceFeedbackResponse Create(AttendanceFeedbackRequest request, Guid actor, Guid submissionId,
        bool edit, byte[] previousVersion)
    {
        if (actor != request.PatientUserId || submissionId == Guid.Empty || previousVersion.Length != 8 ||
            !request.Outcome.HasValue || !request.RespondedAtUtc.HasValue || request.ResponseRevision < 1)
            throw new ArgumentException("A response requires its owner, submission ID and original request version.");
        return new(Guid.NewGuid())
        {
            FeedbackRequestId = request.Id, ActorUserId = actor, SubmissionId = submissionId,
            Revision = request.ResponseRevision, Outcome = request.Outcome.Value,
            SubmittedAtUtc = request.RespondedAtUtc.Value, IsEdit = edit, PreviousVersion = previousVersion.ToArray()
        };
    }
}
