using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLinkMigrationScriptTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task NativeScriptUpgradesExistingIndependentAndManagedBookingsWithoutRewritingScope()
    {
        var attendance = new AttendanceTests(fixture); var independent = await attendance.Setup(); var managed = await attendance.Setup(managed: true);
        await using var db = fixture.Database.CreateContext(); var migrator = db.GetService<IMigrator>();
        var before = await db.Appointments.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Id, a.Version }).ToArrayAsync();
        await migrator.MigrateAsync("20261030173034_PracticeLifecycle");
        var sql = migrator.GenerateScript("20261030173034_PracticeLifecycle", "20261031164704_PracticeLinking");
        await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync();
        await using (var settings = new SqlCommand("SET QUOTED_IDENTIFIER OFF;", connection)) await settings.ExecuteNonQueryAsync();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
        }
        var after = await db.Appointments.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Id, a.Version }).ToArrayAsync();
        Assert.Equal(before.Select(a => a.Id), after.Select(a => a.Id)); Assert.Equal(before.Select(a => Convert.ToBase64String(a.Version)), after.Select(a => Convert.ToBase64String(a.Version)));
        Assert.Null((await db.BookingSessions.SingleAsync(s => s.Id == independent.Session.Id)).OperationalBranchId);
        Assert.Equal(managed.BranchId, (await db.BookingSessions.SingleAsync(s => s.Id == managed.Session.Id)).OperationalBranchId);
        Assert.Empty(await db.PracticeLinkRequests.ToArrayAsync());
        Assert.Equal("20261031164704_PracticeLinking", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name='TR_PracticeLinkRequests_Protect' AND is_disabled=0").SingleAsync());
    }
}
