using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AttendanceMigrationScriptTests(SqlServerIntegrationFixture fixture) : IClassFixture<SqlServerIntegrationFixture>
{
    [SqlServerFact]
    public async Task GeneratedSqlRunsAsBatchesAndCreatesTheImmutableHistoryTrigger()
    {
        await using var db = fixture.CreateContext(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028173849_PatientAppointments");
        var sql = migrator.GenerateScript("20261028173849_PatientAppointments", "20261028200441_AppointmentAttendance");
        await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
        }
        Assert.EndsWith("_AppointmentAttendance", (await db.Database.GetAppliedMigrationsAsync()).Last());
        var triggers = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name='TR_AttendanceRecords_Immutable' AND is_disabled=0").SingleAsync();
        Assert.Equal(1, triggers); Assert.Equal(0, await db.AttendanceRecords.CountAsync());
    }
}
