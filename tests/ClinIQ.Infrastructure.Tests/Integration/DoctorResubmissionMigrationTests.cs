using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorResubmissionMigrationTests(SqlServerIntegrationFixture database) : IClassFixture<SqlServerIntegrationFixture>
{
    [SqlServerFact]
    public async Task UpgradeBackfillsExistingRejectedRegistrationWithoutRewritingItsHistory()
    {
        await using var db = database.CreateContext(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261027142333_DoctorProfessionalChanges");
        var specialty = Specialty.Create("قديم", "Legacy synthetic specialty").Value; db.Add(specialty); await db.SaveChangesAsync();
        var user = Guid.NewGuid(); var reviewer = Guid.NewGuid(); var id = Guid.NewGuid(); var reviewId = Guid.NewGuid(); var now = database.Clock.GetUtcNow();
        foreach (var account in new[] { user, reviewer })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AspNetUsers (Id,FullName,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount) VALUES ({account},N'Synthetic legacy',0,0,0,1,0)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DoctorRegistrations (Id,UserId,ProfessionalName,PhoneNumber,RegistrationNumber,SpecialtyId,ProfessionalGrade,Status,CreatedAtUtc) VALUES ({id},{user},N'Legacy Doctor','01012345678','LEGACY',{specialty.Id},1,3,{now})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DoctorRegistrationReviews (Id,DoctorRegistrationId,ReviewerUserId,Outcome,Reason,ReviewedAtUtc) VALUES ({reviewId},{id},{reviewer},3,'Legacy rejection',{now})");
        var documentIds = new List<Guid>();
        foreach (var kind in new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity })
        {
            var doc = Guid.NewGuid(); documentIds.Add(doc); var key = $"{id:N}/{doc:N}.pdf"; var hash = new string('A', 64);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DoctorEvidenceDocuments (Id,DoctorRegistrationId,Kind,StorageKey,OriginalFileName,MediaType,Length,Sha256,CreatedAtUtc) VALUES ({doc},{id},{(int)kind},{key},'legacy.pdf','application/pdf',10,{hash},{now})");
        }
        byte[] before;
        await using (var connection = new SqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync(); await using var command = new SqlCommand("SELECT [Version] FROM [DoctorRegistrations]", connection);
            before = (byte[])(await command.ExecuteScalarAsync())!;
        }
        await migrator.MigrateAsync();
        var old = await db.DoctorRegistrations.AsNoTracking().Include(x => x.Documents).SingleAsync();
        Assert.True(old.IsCurrent); Assert.Equal(1, old.AttemptNumber); Assert.Null(old.PreviousRegistrationId); Assert.Null(old.ResubmittedByUserId);
        Assert.Equal(DoctorRegistrationStatus.Rejected, old.Status); Assert.Equal("Legacy Doctor", old.ProfessionalName); Assert.Equal(now, old.CreatedAtUtc);
        Assert.Equal(before, old.Version); Assert.Equal(documentIds.Order(), old.Documents.Select(x => x.Id).Order());
        var review = await db.DoctorRegistrationReviews.SingleAsync(); Assert.Equal(reviewId, review.Id); Assert.Equal("Legacy rejection", review.Reason);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
