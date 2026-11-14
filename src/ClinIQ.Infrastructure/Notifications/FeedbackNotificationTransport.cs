using System.Text.Json;
using ClinIQ.Application.Features.Notifications;
using ClinIQ.Infrastructure.Accounts;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ClinIQ.Infrastructure.Notifications;
public sealed class FeedbackNotificationTransport(IOptions<FeedbackNotificationOptions> options, RegistrationStorage storage, IHostEnvironment environment) : IFeedbackNotificationTransport, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private FirebaseApp? app;
    public static Message PushPayload(FeedbackPushMessage m) => new()
    {
        // Flutter supplies a registration token. An installation FID is a different identifier.
#pragma warning disable CS0618
        Token = m.Token,
#pragma warning restore CS0618
        Data = new Dictionary<string, string> { ["type"] = "attendance-feedback", ["deliveryId"] = m.DeliveryId.ToString(), ["feedbackRequestId"] = m.FeedbackRequestId.ToString(), ["recipientUserId"] = m.RecipientUserId.ToString(), ["sessionId"] = m.SessionId.ToString() },
        Android = new AndroidConfig { Priority = Priority.High, TimeToLive = TimeSpan.FromMinutes(5) },
        Apns = new ApnsConfig { Headers = new Dictionary<string, string> { ["apns-push-type"] = "background", ["apns-priority"] = "5" }, Aps = new Aps { ContentAvailable = true } }
    };
    public async Task<NotificationDeliveryResult> PushAsync(FeedbackPushMessage m, CancellationToken ct)
    {
        var o = options.Value;
        if (o.PushMode == "DevelopmentFile" && environment.IsDevelopment()) return await FileAsync(m.DeliveryId, new { Type = "Push", m.DeliveryId, m.FeedbackRequestId, m.RecipientUserId, m.SessionId }, ct);
        if (o.PushMode != "Fcm") return new(DeliveryOutcome.Retry, "Push.NotConfigured");
        try
        {
            await gate.WaitAsync(ct);
            try { app ??= FirebaseApp.Create(new AppOptions { ProjectId = o.FirebaseProjectId, Credential = await GoogleCredential.GetApplicationDefaultAsync(ct) }, "ClinIQ-Feedback-" + Guid.NewGuid().ToString("N")); }
            finally { gate.Release(); }
            return new(DeliveryOutcome.Accepted, ProviderMessageId: await FirebaseMessaging.GetMessaging(app).SendAsync(PushPayload(m), ct));
        }
        catch (FirebaseMessagingException e) when (e.MessagingErrorCode == MessagingErrorCode.Unregistered) { return new(DeliveryOutcome.InvalidDevice, "Push.Unregistered"); }
    }
    public async Task<NotificationDeliveryResult> EmailAsync(FeedbackEmailMessage m, CancellationToken ct)
    {
        var o = options.Value;
        var url = o.MobileFeedbackUrl is null ? null : o.MobileFeedbackUrl + "?feedbackRequestId=" + m.FeedbackRequestId;
        if (o.EmailMode == "DevelopmentFile" && environment.IsDevelopment()) return await FileAsync(m.DeliveryId, new { Type = "EmailReminder", m.DeliveryId, m.FeedbackRequestId, To = m.Email, MobileUrl = url }, ct);
        if (o.EmailMode != "Smtp") return new(DeliveryOutcome.Retry, "Email.NotConfigured");
        var message = new MimeMessage { MessageId = m.DeliveryId.ToString("N") + "@" + MailboxAddress.Parse(o.FromEmail!).Domain, Subject = "ClinIQ: attendance response reminder", Body = new TextPart("plain") { Text = "You have a pending attendance response in ClinIQ. Open the mobile app: " + url } };
        message.From.Add(MailboxAddress.Parse(o.FromEmail!)); message.To.Add(MailboxAddress.Parse(m.Email));
        using var client = new SmtpClient { Timeout = 20000 };
        await client.ConnectAsync(o.SmtpHost!, o.SmtpPort, o.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrEmpty(o.SmtpUser)) await client.AuthenticateAsync(o.SmtpUser, o.SmtpPassword ?? "", ct);
        await client.SendAsync(message, ct);
        // The SMTP server has accepted the message; a disconnect failure must not schedule another send.
        try { await client.DisconnectAsync(true, ct); } catch (Exception) { }
        return new(DeliveryOutcome.Accepted, ProviderMessageId: message.MessageId);
    }
    private async Task<NotificationDeliveryResult> FileAsync(Guid id, object message, CancellationToken ct)
    {
        var folder = Path.Combine(storage.Root, "development-feedback-notifications"); Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, id.ToString("N") + ".json");
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(message), ct); File.Move(temporary, destination, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(DeliveryOutcome.DevelopmentFile);
    }
    public void Dispose() { app?.Delete(); gate.Dispose(); }
}
