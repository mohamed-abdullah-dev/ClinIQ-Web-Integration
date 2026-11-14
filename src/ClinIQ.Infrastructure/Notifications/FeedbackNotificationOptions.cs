namespace ClinIQ.Infrastructure.Notifications;
public sealed class FeedbackNotificationOptions
{
    public bool WorkerEnabled { get; set; } = true;
    public string PushMode { get; set; } = "Disabled";
    public string EmailMode { get; set; } = "Disabled";
    public string? FirebaseProjectId { get; set; }
    public string? MobileFeedbackUrl { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? FromEmail { get; set; }
    public bool Valid() => (PushMode is "Disabled" or "DevelopmentFile" or "Fcm") && (EmailMode is "Disabled" or "DevelopmentFile" or "Smtp") &&
        (PushMode != "Fcm" || !string.IsNullOrWhiteSpace(FirebaseProjectId)) &&
        (EmailMode != "Smtp" || (!string.IsNullOrWhiteSpace(SmtpHost) && SmtpPort is > 0 and < 65536 && !string.IsNullOrWhiteSpace(FromEmail) &&
            Uri.TryCreate(MobileFeedbackUrl, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0));
}
