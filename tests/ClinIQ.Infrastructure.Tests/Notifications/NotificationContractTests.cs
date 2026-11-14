using ClinIQ.Application.Features.Notifications;
using ClinIQ.Application.Features.Notifications.Commands;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Notifications;
using Xunit;
namespace ClinIQ.Infrastructure.Tests.Notifications;
public sealed class NotificationContractTests
{
    [Fact]
    public void InvalidDeviceProofAndTokenInputAreRejected()
    {
        var v=new PushDeviceInputValidator(); var input=new RegisterPushDeviceInput(Guid.NewGuid(),new string('a',43),"synthetic-token",PushPlatform.Android);
        Assert.True(v.Validate(input).IsValid);
        foreach(var bad in new[]{input with{InstallationId=Guid.Empty},input with{InstallationSecret="guess"},input with{Token="a\nb"},input with{Token=new string('a',4097)},input with{Platform=(PushPlatform)3}}) Assert.False(v.Validate(bad).IsValid);
    }
    [Fact]
    public void PushPayloadHasNoAutomaticDisplayOrMedicalContentAndCarriesAccountSessionAndDedupIdentity()
    {
        var m=new FeedbackPushMessage(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"synthetic"); var payload=FeedbackNotificationTransport.PushPayload(m);
        Assert.Null(payload.Notification); Assert.Equal(5,payload.Data.Count); Assert.Equal(m.SessionId.ToString(),payload.Data["sessionId"]); Assert.Equal(m.RecipientUserId.ToString(),payload.Data["recipientUserId"]);
        Assert.Equal(m.DeliveryId.ToString(),payload.Data["deliveryId"]); Assert.Equal(TimeSpan.FromMinutes(5),payload.Android.TimeToLive);
    }
    [Fact]
    public void RetryBackoffRemainsBoundedAcrossRepeatedTransportFailures()
    {
        Assert.Equal(TimeSpan.FromMinutes(1),FeedbackDelivery.Backoff(1)); Assert.Equal(TimeSpan.FromHours(1),FeedbackDelivery.Backoff(int.MaxValue));
    }
    [Fact]
    public void LiveTransportsRequireExplicitValidConfiguration()
    {
        var o=new FeedbackNotificationOptions(); Assert.True(o.Valid()); o.PushMode="Fcm"; Assert.False(o.Valid()); o.FirebaseProjectId="synthetic"; Assert.True(o.Valid());
        o.EmailMode="Smtp"; Assert.False(o.Valid()); o.SmtpHost="smtp.example.test";o.FromEmail="test@example.test";o.MobileFeedbackUrl="https://example.test/mobile-feedback";Assert.True(o.Valid());
        o.MobileFeedbackUrl="https://example.test/mobile-feedback?redirect=evil";Assert.False(o.Valid());
    }
}
