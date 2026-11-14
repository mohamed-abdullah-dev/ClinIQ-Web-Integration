using ClinIQ.Domain.PracticeLocations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class WorkforceUpgradeTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task UpgradeKeepsExistingIndependentLocationActiveWithSameIdentityFeeAndSubmission()
    {
        var (_, place) = await new PremisesCorrectionTests(fixture).Place();
        await using var db = fixture.Database.CreateContext(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028134115_PremisesCorrections");
        try
        {
            var legacy = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM DoctorPracticeLocations WHERE Id={place.Id} AND ConsultationFee={place.ConsultationFee}").SingleAsync(); Assert.Equal(1, legacy);
        }
        finally { await migrator.MigrateAsync(); }
        var after = await db.DoctorPracticeLocations.SingleAsync(x => x.Id == place.Id);
        Assert.True(after.IsActive); Assert.Null(after.ClinicBranchId); Assert.Null(after.SourceInvitationId); Assert.Equal(place.CurrentSubmissionId, after.CurrentSubmissionId);
        Assert.Equal(place.ConsultationFee, after.ConsultationFee); Assert.Equal(PracticeLocationStatus.Approved, after.Status); Assert.Equal(place.UserId, after.UserId);
    }
}
