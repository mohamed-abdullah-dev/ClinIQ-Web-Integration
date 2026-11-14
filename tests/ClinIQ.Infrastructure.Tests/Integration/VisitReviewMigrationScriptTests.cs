using System.Text.RegularExpressions;
using ClinIQ.Domain.Appointments;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class VisitReviewMigrationScriptTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task ActualSqlUpgradesExistingAttendanceWithQuotedIdentifierOffWithoutChangingFacts()
    {
        await using var db = fixture.Database.CreateContext(); var migrator = db.GetService<IMigrator>();
        var s = await new VisitReviewTests(fixture).Completed();
        await migrator.MigrateAsync("20261029160441_FeedbackNotifications");
        var original = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == s.Rows[0].Id);
        var sql = migrator.GenerateScript("20261029160441_FeedbackNotifications", "20261030153407_VisitReviews");
        await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync();
        await using (var defaults = new SqlCommand("SET QUOTED_IDENTIFIER OFF;", connection)) await defaults.ExecuteNonQueryAsync();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
        }
        var after = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == original.Id);
        Assert.Equal(AppointmentStatus.Completed, after.Status); Assert.Equal(original.CompletedAtUtc, after.CompletedAtUtc);
        Assert.Equal(original.Version, after.Version); Assert.Empty(await db.VisitReviews.ToArrayAsync());
        Assert.Equal("20261030153407_VisitReviews", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name='TR_VisitReviewAudits_Immutable' AND is_disabled=0").SingleAsync());
    }
}
