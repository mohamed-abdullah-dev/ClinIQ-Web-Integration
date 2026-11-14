using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common;
using ClinIQ.Domain.Common.Results;
namespace ClinIQ.Domain.VisitReviews;
public enum ReviewModerationCategory { Abusive = 1, PrivateInformation = 2, Irrelevant = 3 }
public sealed class VisitReview : Entity
{
    public Guid AppointmentId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid PatientUserId { get; private set; }
    public Guid DoctorProfileId { get; private set; }
    public Guid LocationId { get; private set; }
    public string LocationName { get; private set; } = null!;
    public int Stars { get; private set; }
    public string? Text { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? EditedAtUtc { get; private set; }
    public bool Hidden { get; private set; }
    public ReviewModerationCategory? ModerationCategory { get; private set; }
    public string? ModerationReason { get; private set; }
    public Guid? ModeratedByUserId { get; private set; }
    public DateTimeOffset? ModeratedAtUtc { get; private set; }
    public int Revision { get; private set; }
    public byte[] Version { get; private set; } = [];
    private VisitReview() { }
    private VisitReview(Guid id) : base(id) { }
    public static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    public static Error? Eligibility(Appointment a, DateTimeOffset now)
    {
        if (a.Status != AppointmentStatus.Completed || !a.CompletedAtUtc.HasValue) return VisitReviewErrors.Ineligible;
        return now < a.CompletedAtUtc || now >= a.CompletedAtUtc.Value.AddDays(30) ? VisitReviewErrors.Expired : null;
    }
    public static Result<VisitReview> Create(Appointment a, int stars, string? text, DateTimeOffset now)
    {
        if (Eligibility(a,now) is {} error) return error;
        if (stars is < 1 or > 5 || Normalize(text)?.Length > 2000) return VisitReviewErrors.Input;
        return new VisitReview(Guid.NewGuid()) { AppointmentId=a.Id, PatientId=a.PatientId, PatientUserId=a.PatientUserId, DoctorProfileId=a.DoctorProfileId,
            LocationId=a.LocationId, LocationName=a.PlaceName, Stars=stars,Text=Normalize(text),CreatedAtUtc=now,Revision=1 };
    }
    public Result<bool> Edit(Appointment a,int stars,string? text,DateTimeOffset now)
    {
        if (a.Id!=AppointmentId) throw new ArgumentException("Review appointment mismatch.");
        if (Eligibility(a,now) is {} error) return error;
        if(stars is <1 or >5 || Normalize(text)?.Length>2000) return VisitReviewErrors.Input;
        if(Stars==stars && Text==Normalize(text)) return false;
        Stars=stars;Text=Normalize(text);EditedAtUtc=now;Revision=checked(Revision+1);return true;
    }
    public Result<bool> Moderate(bool hide,ReviewModerationCategory? category,string reason,Guid actor,DateTimeOffset now)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>2000 || (hide ? !category.HasValue||!Enum.IsDefined(category.Value) : category.HasValue)) return VisitReviewErrors.Moderation;
        if(actor==Guid.Empty)throw new ArgumentException("Moderator required.");
        if(Hidden==hide && ModerationCategory==category && ModerationReason==reason.Trim())return false;
        Hidden=hide;ModerationCategory=category;ModerationReason=reason.Trim();ModeratedByUserId=actor;ModeratedAtUtc=now;Revision=checked(Revision+1);return true;
    }
}
public sealed class VisitReviewAudit : Entity
{
    public Guid ReviewId {get;private set;}
    public Guid ActorUserId {get;private set;}
    public int Revision {get;private set;}
    public string Action {get;private set;}=null!;
    public string BeforeJson {get;private set;}=null!;
    public string AfterJson {get;private set;}=null!;
    public DateTimeOffset RecordedAtUtc {get;private set;}
    public byte[] AppointmentVersion {get;private set;}=[];
    private VisitReviewAudit(){}
    private VisitReviewAudit(Guid id):base(id){}
    public static VisitReviewAudit Create(VisitReview review,Guid actor,string action,string before,string after,byte[] appointmentVersion,DateTimeOffset now)
    {
        if(actor==Guid.Empty||appointmentVersion.Length!=8||action is not ("Create" or "Edit" or "Hide" or "Restore"))throw new ArgumentException("Invalid review audit.");
        return new(Guid.NewGuid()){ReviewId=review.Id,ActorUserId=actor,Revision=review.Revision,Action=action,BeforeJson=before,AfterJson=after,RecordedAtUtc=now,AppointmentVersion=appointmentVersion.ToArray()};
    }
}
public static class VisitReviewErrors
{
    public static readonly Error Unauthorized=Error.Unauthorized("VisitReviews.Unauthorized","Please log in.");
    public static readonly Error Forbidden=Error.Forbidden("VisitReviews.Forbidden","This role or client cannot perform the requested review action.");
    public static readonly Error NotFound=Error.NotFound("VisitReviews.NotFound","The booking or review is unavailable.");
    public static readonly Error Ineligible=Error.Conflict("VisitReviews.Ineligible","Only a genuinely Completed appointment is eligible for a review.");
    public static readonly Error Expired=Error.Conflict("VisitReviews.Expired","The thirty-day review window from factual consultation completion is closed.");
    public static readonly Error Input=Error.Validation("VisitReviews.Input","Provide 1 to 5 stars and optional text up to 2000 characters.");
    public static readonly Error Exists=Error.Conflict("VisitReviews.Exists","This booking already has a review. Use explicit edit with its current version.");
    public static readonly Error Stale=Error.Conflict("VisitReviews.StaleVersion","Reload the review before retrying.");
    public static readonly Error Busy=Error.Conflict("VisitReviews.Busy","The booking is being updated. Retry shortly.");
    public static readonly Error Moderation=Error.Validation("VisitReviews.Moderation","Hiding requires an abusive/private/irrelevant category and a reason; restoring requires a reason and no category.");
}
