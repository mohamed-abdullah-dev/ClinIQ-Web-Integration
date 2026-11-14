using System.Text.RegularExpressions;
using System.Text.Json;
using ClinIQ.Domain.Appointments;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class FeedbackMigrationScriptTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task GeneratedScriptRunsInSqlServerBatchesAndCreatesBothProtectionTriggers()
    {
        await using var db = fixture.Database.CreateContext(); var migrator = db.GetService<IMigrator>();
        // Seed a genuine legacy P09 NoShow before feedback tables exist.
        var legacy = await LegacyAttendanceSeed.CreateAsync(fixture, "20261028200441_AppointmentAttendance");
        var appointment = await db.Appointments.SingleAsync(a => a.Id == legacy.Appointment.Id);
        var input = new AttendanceInput(appointment.PatientId, AttendanceAction.NoShow, Convert.ToBase64String(appointment.Version));
        var version = appointment.Version.ToArray(); var eventTime = appointment.EndsAtUtc;
        var change = appointment.RecordAttendance(input, legacy.Doctor, true, false, eventTime, null).Value;
        var source = AttendanceRecord.Create(appointment, legacy.Doctor, eventTime, input, change,
            JsonSerializer.Serialize(change.Before), JsonSerializer.Serialize(change.After), version);
        db.AttendanceRecords.Add(source); await db.SaveChangesAsync();
        fixture.Database.Clock.UtcNow = eventTime.AddDays(8);
        var script = migrator.GenerateScript("20261028200441_AppointmentAttendance", "20261029143805_AttendanceFeedback");
        await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync();
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
        }
        Assert.EndsWith("_AttendanceFeedback", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name IN ('TR_AttendanceFeedbackResponses_Immutable','TR_AttendanceFeedbackRequests_Protect') AND is_disabled=0").SingleAsync());
        var request = Assert.Single(await db.AttendanceFeedbackRequests.AsNoTracking().ToArrayAsync());
        Assert.Equal(source.Id, request.SourceAttendanceRecordId); Assert.Equal(eventTime, request.CreatedAtUtc);
        Assert.Equal(eventTime.AddDays(7), request.ExpiresAtUtc); Assert.True(request.ExpiresAtUtc < fixture.Database.Clock.GetUtcNow());
        Assert.Null(request.Outcome); Assert.Null(request.ReviewState);
    }
}
