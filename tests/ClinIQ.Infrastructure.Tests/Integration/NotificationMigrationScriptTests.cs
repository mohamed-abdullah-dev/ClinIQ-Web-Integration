using System.Text.Json;
using System.Text.RegularExpressions;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
namespace ClinIQ.Infrastructure.Tests.Integration;
[Trait("Category","SqlServerIntegration")]
public sealed class NotificationMigrationScriptTests(DoctorReviewFixture fixture):IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task ActualSqlBackfillsOriginalDueTimesAndInstallsSessionRevocationTrigger()
    {
        await using var db=fixture.Database.CreateContext();var migrator=db.GetService<IMigrator>();
        var legacy=await LegacyAttendanceSeed.CreateAsync(fixture,"20261029143805_AttendanceFeedback");var a=await db.Appointments.SingleAsync(x=>x.Id==legacy.Appointment.Id);var version=a.Version.ToArray();var now=a.EndsAtUtc;
        var input=new AttendanceInput(a.PatientId,AttendanceAction.NoShow,Convert.ToBase64String(version));var change=a.RecordAttendance(input,legacy.Doctor,true,false,now,null).Value;
        var record=AttendanceRecord.Create(a,legacy.Doctor,now,input,change,JsonSerializer.Serialize(change.Before),JsonSerializer.Serialize(change.After),version);
        var request=AttendanceFeedbackRequest.Create(a,record);db.AttendanceRecords.Add(record);db.AttendanceFeedbackRequests.Add(request);await db.SaveChangesAsync();
        var sql=migrator.GenerateScript("20261029143805_AttendanceFeedback","20261029160441_FeedbackNotifications");
        await using var connection=new SqlConnection(db.Database.GetConnectionString());await connection.OpenAsync();
        await using(var defaults=new SqlCommand("SET QUOTED_IDENTIFIER OFF;",connection)) await defaults.ExecuteNonQueryAsync();
        foreach(var batch in Regex.Split(sql,@"^\s*GO\s*$",RegexOptions.Multiline|RegexOptions.IgnoreCase)){if(string.IsNullOrWhiteSpace(batch))continue;await using var command=new SqlCommand(batch,connection){CommandTimeout=30};await command.ExecuteNonQueryAsync();}
        var notices=await db.FeedbackNotices.Where(n=>n.FeedbackRequestId==request.Id).ToArrayAsync();Assert.Equal(2,notices.Length);
        Assert.Equal(now,notices.Single(n=>n.Kind==FeedbackNoticeKind.InitialPush).DueAtUtc);Assert.Equal(now.AddHours(24),notices.Single(n=>n.Kind==FeedbackNoticeKind.EmailReminder).DueAtUtc);
        Assert.Equal(1,await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name='TR_AuthSessions_DetachPush' AND is_disabled=0").SingleAsync());
    }
}
