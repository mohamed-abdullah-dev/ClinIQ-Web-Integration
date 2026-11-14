using System.Security.Claims;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Notifications;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
namespace ClinIQ.Infrastructure.Tests.Integration;
[Trait("Category", "SqlServerIntegration")]
public sealed class FeedbackNotificationTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private Task<SessionGrant> Login(Guid user) => fixture.AsAsync(user, async p =>
    {
        await using var db = fixture.Database.CreateContext(); var email = (await db.Users.SingleAsync(u => u.Id == user)).Email!;
        return (await p.GetRequiredService<ISessionService>().LoginAsync(email, DoctorReviewFixture.Password, true, SessionClient.Mobile, default)).Value;
    });
    private Task<T> As<T>(SessionGrant grant, Func<IServiceProvider,Task<T>> run) => fixture.AsAsync(grant.User.Id, p =>
    {
        var identity=(ClaimsIdentity)p.GetRequiredService<IHttpContextAccessor>().HttpContext!.User.Identity!;
        identity.AddClaim(new("client","Mobile")); identity.AddClaim(new("session_id",grant.SessionId.ToString())); return run(p);
    });
    private Task<ClinIQ.Domain.Common.Results.Result<PushDeviceDto>> Bind(SessionGrant session, RegisterPushDeviceInput input) => As(session,p=>p.GetRequiredService<IPushDeviceService>().RegisterAsync(input,default));
    private Task<FeedbackDispatchResult> Run() => fixture.AsAsync(null,p=>p.GetRequiredService<IFeedbackDispatcher>().RunAsync(default));
    private static RegisterPushDeviceInput Device() => new(Guid.NewGuid(),SessionSecrets.Create(),"synthetic-"+Guid.NewGuid().ToString("N"),PushPlatform.Android);
    private async Task<(AttendanceTests.Scenario Scenario,SessionGrant Grant,Guid Request)> Setup()
    {
        var helper=new AttendanceTests(fixture); var s=await helper.Setup(); fixture.Database.Clock.UtcNow=s.Session.EndsAtUtc;
        var grant=await Login(s.Patients[0]); Assert.True((await helper.Write(s.Doctor,s,s.Rows[0],AttendanceAction.NoShow)).IsSuccess);
        await using var db=fixture.Database.CreateContext(); var id=await db.AttendanceFeedbackRequests.Where(r=>r.AppointmentId==s.Rows[0].Id).Select(r=>r.Id).SingleAsync(); return(s,grant,id);
    }
    [SqlServerFact]
    public async Task DeviceTokenIsEncryptedRetryIsIdempotentAndWrongProofCannotSwitchOwnership()
    {
        var patient=await new AppointmentTests(fixture).Patient(); var grant=await Login(patient); var input=Device();
        var first=(await Bind(grant,input)).Value; var second=(await Bind(grant,input)).Value; Assert.Equal(first,second);
        var other=await Login(await new AppointmentTests(fixture).Patient());
        Assert.Equal(NotificationErrors.Proof,Assert.Single((await Bind(other,input with{InstallationSecret=SessionSecrets.Create()})).Errors));
        Assert.Equal(NotificationErrors.TokenInUse,Assert.Single((await Bind(other,Device() with{Token=input.Token})).Errors));
        await using var db=fixture.Database.CreateContext(); var stored=await db.PushDevices.SingleAsync(d=>d.Id==input.InstallationId);
        Assert.NotEqual(input.Token,stored.ProtectedToken); Assert.NotEqual(input.InstallationSecret,stored.InstallationSecretHash); Assert.Equal(patient,stored.UserId);
    }
    [SqlServerFact]
    public async Task SwitchingWithInstallationProofDetachesOldAccountAndOldLogoutCannotDetachNewBinding()
    {
        var a=await Login(await new AppointmentTests(fixture).Patient()); var b=await Login(await new AppointmentTests(fixture).Patient()); var input=Device();
        await Bind(a,input); var moved=(await Bind(b,input)).Value; Assert.Equal(2,moved.BindingRevision);
        await fixture.AsAsync(null,p=>p.GetRequiredService<ISessionService>().LogoutAsync(a.RefreshToken,SessionClient.Mobile,default));
        await using var db=fixture.Database.CreateContext(); var device=await db.PushDevices.SingleAsync(d=>d.Id==input.InstallationId); Assert.Equal(b.User.Id,device.UserId);
        await fixture.AsAsync(null,p=>p.GetRequiredService<ISessionService>().LogoutAsync(b.RefreshToken,SessionClient.Mobile,default));
        await db.Entry(device).ReloadAsync(); Assert.Null(device.UserId); Assert.Null(device.SessionId); Assert.Null(device.TokenHash); Assert.Null(device.ProtectedToken);
    }
    [SqlServerFact]
    public async Task BulkSessionRevocationAlsoRemovesBindingsAtomically()
    {
        var grant=await Login(await new AppointmentTests(fixture).Patient()); var input=Device(); await Bind(grant,input);
        await using var db=fixture.Database.CreateContext(); var now=fixture.Database.Clock.GetUtcNow();
        await db.AuthSessions.Where(s=>s.Id==grant.SessionId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RevokedAtUtc,now));
        Assert.Null((await db.PushDevices.SingleAsync(d=>d.Id==input.InstallationId)).UserId);
        Assert.True((await Bind(grant,input)).IsError);
    }
    [SqlServerFact]
    public async Task MultipleDevicesReceiveOnePushEachAndExactlyOneReminderBecomesDueAt24Hours()
    {
        var x=await Setup(); await Bind(x.Grant,Device()); await Bind(x.Grant,Device());
        await using var db=fixture.Database.CreateContext(); var request=await db.AttendanceFeedbackRequests.SingleAsync(r=>r.Id==x.Request);
        Assert.Equal(2,await db.FeedbackNotices.CountAsync(n=>n.FeedbackRequestId==x.Request));
        await Task.WhenAll(Run(),Run());
        var notice=await db.FeedbackNotices.SingleAsync(n=>n.FeedbackRequestId==x.Request && n.Kind==FeedbackNoticeKind.InitialPush);
        var pushes=await db.FeedbackDeliveries.Where(d=>d.NoticeId==notice.Id).ToArrayAsync(); Assert.Equal(2,pushes.Length); Assert.All(pushes,d=>Assert.Equal(FeedbackDeliveryState.DevelopmentFile,d.State));
        fixture.Database.Clock.UtcNow=request.CreatedAtUtc.AddHours(24).AddTicks(-1); await Run();
        var email=await db.FeedbackNotices.AsNoTracking().SingleAsync(n=>n.FeedbackRequestId==x.Request && n.Kind==FeedbackNoticeKind.EmailReminder);
        Assert.False(await db.FeedbackDeliveries.AnyAsync(d=>d.NoticeId==email.Id));
        fixture.Database.Clock.UtcNow=request.CreatedAtUtc.AddHours(24); await Task.WhenAll(Run(),Run()); await Run();
        var delivery=Assert.Single(await db.FeedbackDeliveries.Where(d=>d.NoticeId==email.Id).ToArrayAsync()); Assert.Equal(1,delivery.Attempts); Assert.Equal(FeedbackDeliveryState.DevelopmentFile,delivery.State);
    }
    [SqlServerFact]
    public async Task TransportFailureRetriesTheSameDeliveryAndNeverChangesNoShow()
    {
        var x=await Setup(); await Bind(x.Grant,Device()); var options=fixture.Services.GetRequiredService<IOptions<FeedbackNotificationOptions>>().Value; options.PushMode="Disabled";
        try
        {
            await Run(); await using var db=fixture.Database.CreateContext(); var notice=await db.FeedbackNotices.SingleAsync(n=>n.FeedbackRequestId==x.Request && n.Kind==FeedbackNoticeKind.InitialPush);
            var delivery=await db.FeedbackDeliveries.SingleAsync(d=>d.NoticeId==notice.Id); Assert.Equal(FeedbackDeliveryState.Retry,delivery.State); Assert.Equal(1,delivery.Attempts);
            await Run(); await db.Entry(delivery).ReloadAsync(); Assert.Equal(1,delivery.Attempts);
            fixture.Database.Clock.UtcNow=delivery.NextAttemptAtUtc; options.PushMode="DevelopmentFile"; await Run(); await db.Entry(delivery).ReloadAsync();
            Assert.Equal(FeedbackDeliveryState.DevelopmentFile,delivery.State); Assert.Equal(2,delivery.Attempts); Assert.Equal(1,await db.FeedbackDeliveries.CountAsync(d=>d.NoticeId==notice.Id));
            Assert.Equal(AppointmentStatus.NoShow,(await db.Appointments.SingleAsync(a=>a.Id==x.Scenario.Rows[0].Id)).Status);
        }
        finally{options.PushMode="DevelopmentFile";}
    }
    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnswerOrStaffCorrectionStopsTheReminder(bool answer)
    {
        var x=await Setup();
        if(answer) await As(x.Grant,async p=>{var service=p.GetRequiredService<IAttendanceFeedbackService>(); var current=(await service.GetAsync(x.Request,default)).Value; return await service.AnswerAsync(x.Request,new(Guid.NewGuid(),PatientAttendanceOutcome.DidNotAttend,current.Version),false,default);});
        else {var helper=new AttendanceTests(fixture); var current=await helper.Current(x.Scenario.Doctor,x.Scenario,x.Scenario.Rows[0].Id); Assert.True((await helper.Write(x.Scenario.Doctor,x.Scenario,current,AttendanceAction.Correct,target:AppointmentStatus.Confirmed,reason:"Correction")).IsSuccess);}
        fixture.Database.Clock.UtcNow=fixture.Database.Clock.GetUtcNow().AddHours(24); await Run(); await using var db=fixture.Database.CreateContext();
        var email=await db.FeedbackNotices.SingleAsync(n=>n.FeedbackRequestId==x.Request && n.Kind==FeedbackNoticeKind.EmailReminder);
        Assert.NotNull(email.CancelledAtUtc); Assert.False(await db.FeedbackDeliveries.AnyAsync(d=>d.NoticeId==email.Id));
    }
    [SqlServerFact]
    public async Task SendingLeaseRecoversTheSameLogicalDeliveryAfterAWorkerStops()
    {
        var x=await Setup(); await Bind(x.Grant,Device()); await Run(); await using var db=fixture.Database.CreateContext();
        var notice=await db.FeedbackNotices.SingleAsync(n=>n.FeedbackRequestId==x.Request && n.Kind==FeedbackNoticeKind.InitialPush);
        var delivery=await db.FeedbackDeliveries.SingleAsync(d=>d.NoticeId==notice.Id); var now=fixture.Database.Clock.GetUtcNow();var lease=now.AddMinutes(2);var claim=Guid.NewGuid();
        await db.FeedbackDeliveries.Where(d=>d.Id==delivery.Id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.State,FeedbackDeliveryState.Sending).SetProperty(d=>d.LeaseUntilUtc,lease).SetProperty(d=>d.ClaimId,claim).SetProperty(d=>d.NextAttemptAtUtc,now));
        await Run();await db.Entry(delivery).ReloadAsync();Assert.Equal(1,delivery.Attempts);
        fixture.Database.Clock.UtcNow=lease;await Run();await db.Entry(delivery).ReloadAsync();Assert.Equal(2,delivery.Attempts);Assert.Equal(FeedbackDeliveryState.DevelopmentFile,delivery.State);
        Assert.Equal(1,await db.FeedbackDeliveries.CountAsync(d=>d.NoticeId==notice.Id));
    }
    [SqlServerFact]
    public async Task ExpirySuppressesNotificationsAndExpiredSessionsLoseTheirTokenMaterial()
    {
        var x=await Setup(); var input=Device(); await Bind(x.Grant,input); fixture.Database.Clock.UtcNow=fixture.Database.Clock.GetUtcNow().AddDays(7); await Run();
        await using var db=fixture.Database.CreateContext(); Assert.Null((await db.PushDevices.SingleAsync(d=>d.Id==input.InstallationId)).ProtectedToken);
        Assert.All(await db.FeedbackNotices.Where(n=>n.FeedbackRequestId==x.Request).ToArrayAsync(),n=>Assert.NotNull(n.CancelledAtUtc));
    }
}
