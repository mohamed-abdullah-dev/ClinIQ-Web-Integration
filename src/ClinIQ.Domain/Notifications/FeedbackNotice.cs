using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common;

namespace ClinIQ.Domain.Notifications;

public enum FeedbackNoticeKind { InitialPush = 1, EmailReminder = 2 }
public sealed class FeedbackNotice : Entity
{
    public Guid FeedbackRequestId { get; private set; }
    public FeedbackNoticeKind Kind { get; private set; }
    public DateTimeOffset DueAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? ExpandedAtUtc { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }
    private FeedbackNotice() { }
    private FeedbackNotice(Guid id) : base(id) { }
    public static FeedbackNotice Create(AttendanceFeedbackRequest request, FeedbackNoticeKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Invalid feedback notice kind.");
        var due = kind == FeedbackNoticeKind.InitialPush ? request.CreatedAtUtc : request.CreatedAtUtc.AddHours(24);
        return new(Guid.NewGuid()) { FeedbackRequestId = request.Id, Kind = kind, DueAtUtc = due, NextAttemptAtUtc = due };
    }
    public void Expand(DateTimeOffset now) { ExpandedAtUtc = now; LastErrorCode = null; }
    public void WaitForDevice(DateTimeOffset now) { NextAttemptAtUtc = now.AddMinutes(1); LastErrorCode = "Push.NoActiveDevice"; }
    public void Cancel(DateTimeOffset now) { CancelledAtUtc = now; LastErrorCode = "Feedback.NotApplicable"; }
}
