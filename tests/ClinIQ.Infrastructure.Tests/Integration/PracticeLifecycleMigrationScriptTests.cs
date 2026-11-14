using System.Text.RegularExpressions;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.PracticeLocations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLifecycleMigrationScriptTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task NativeScriptUpgradesExistingBookingAndPreservesInactiveFlagsWithQuotedIdentifierOff()
    {
        var s = await new AttendanceTests(fixture).Setup(managed: true);
        await using var db = fixture.Database.CreateContext(); var migrator = db.GetService<IMigrator>();
        var old = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == s.Rows[0].Id);
        await migrator.MigrateAsync("20261030153407_VisitReviews");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET IsActive=0 WHERE Id={s.Session.LocationId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicBranches SET IsActive=0 WHERE Id={s.BranchId}");
        var sql = migrator.GenerateScript("20261030153407_VisitReviews", "20261030173034_PracticeLifecycle");
        await using var connection = new SqlConnection(db.Database.GetConnectionString()); await connection.OpenAsync();
        await using (var settings = new SqlCommand("SET QUOTED_IDENTIFIER OFF;", connection)) await settings.ExecuteNonQueryAsync();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
        }
        var after = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == old.Id);
        Assert.Equal(AppointmentStatus.Confirmed, after.Status); Assert.Equal(old.Version, after.Version);
        // This checkpoint deliberately predates linking; only project columns that existed then.
        var location = await db.DoctorPracticeLocations.AsNoTracking().Where(p => p.Id == s.Session.LocationId)
            .Select(p => new { p.IsActive, p.LifecycleState }).SingleAsync();
        var branch = await db.ClinicBranches.AsNoTracking().SingleAsync(b => b.Id == s.BranchId);
        Assert.False(location.IsActive); Assert.False(branch.IsActive);
        Assert.Equal(PracticeLifecycleState.Active, location.LifecycleState); Assert.Equal(BranchLifecycleState.Open, branch.LifecycleState);
        Assert.Equal(3, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.triggers WHERE name IN ('TR_OperationalScopeRecords_Immutable','TR_OperationalEmergencies_Protect','TR_EmergencyCancellationRecords_Immutable') AND is_disabled=0").SingleAsync());
        Assert.Equal("20261030173034_PracticeLifecycle", (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Empty(await db.OperationalEmergencies.ToArrayAsync());
        await migrator.MigrateAsync();
    }
}
