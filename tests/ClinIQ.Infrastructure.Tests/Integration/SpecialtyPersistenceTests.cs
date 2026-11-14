using ClinIQ.Domain.Specialties;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SpecialtyPersistenceTests : IClassFixture<SqlServerIntegrationFixture>
{
    private readonly SqlServerIntegrationFixture _database;
    private readonly ITestOutputHelper _output;

    public SpecialtyPersistenceTests(SqlServerIntegrationFixture database, ITestOutputHelper output)
    {
        _database = database;
        _output = output;
        database.Clock.UtcNow = SqlServerIntegrationFixture.InitialUtc;
    }

    [SqlServerFact]
    public async Task Migration_CreatesExpectedTables_AndCanRunAgain()
    {
        _output.WriteLine($"SQLSERVER_TEST_DATABASE:{_database.DatabaseName}");
        await using var context = _database.CreateContext();

        await context.Database.MigrateAsync();

        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(21, migrations.Length);
        Assert.Equal("20261025113854_InitialCreate", migrations[0]);
        Assert.EndsWith("_DoctorRegistration", migrations[1]);
        Assert.EndsWith("_AuthSessions", migrations[2]);
        Assert.EndsWith("_DoctorReview", migrations[3]);
        Assert.EndsWith("_PatientProfile", migrations[4]);
        Assert.EndsWith("_SpecialtyAdministration", migrations[5]);
        Assert.EndsWith("_DoctorProfessionalChanges", migrations[6]);
        Assert.EndsWith("_DoctorResubmission", migrations[7]);
        Assert.EndsWith("_IndependentPracticeLocations", migrations[8]);
        Assert.EndsWith("_ManagedClinicOnboarding", migrations[9]);
        Assert.EndsWith("_PremisesCorrections", migrations[10]);
        Assert.EndsWith("_WorkInvitations", migrations[11]);
        Assert.EndsWith("_DoctorScheduling", migrations[12]);
        Assert.EndsWith("_PatientAppointments", migrations[13]);
        Assert.EndsWith("_AppointmentAttendance", migrations[14]);
        Assert.EndsWith("_AttendanceFeedback", migrations[15]);
        Assert.EndsWith("_FeedbackNotifications", migrations[16]);
        Assert.EndsWith("_VisitReviews", migrations[17]);
        Assert.EndsWith("_PracticeLifecycle", migrations[18]);
        Assert.EndsWith("_PracticeLinking", migrations[19]);
        Assert.EndsWith("_DoctorProTrials", migrations[20]);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        var tables = await context.Database.SqlQueryRaw<string>(
            "SELECT [name] AS [Value] FROM sys.tables WHERE [is_ms_shipped] = 0").ToArrayAsync();
        Assert.Equal(58, tables.Length);
        foreach (var table in new[] { "Specialties", "AspNetUsers", "AspNetRoles", "AspNetUserClaims",
                     "AspNetRoleClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "__EFMigrationsHistory",
                     "DoctorRegistrations", "DoctorEvidenceDocuments", "AuthSessions", "SessionRefreshTokens",
                     "DoctorProfiles", "DoctorRegistrationReviews", "DoctorEvidenceAccesses", "PatientProfiles", "SpecialtyCatalogChanges",
                     "DoctorProfessionalChanges", "ProfessionalChangeDocuments", "ProfessionalChangeEvidenceAccesses",
                     "Appointments", "AppointmentOperations", "BookingNotices", "AttendanceRecords" })
            Assert.Contains(table, tables);
    }

    [SqlServerFact]
    public async Task SaveAndReload_PreservesDisplayKeysIdAndCreationTime()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var specialty = Specialty.Create($"  طب الأطفال {suffix}  ", $"  Pediatrics {suffix}  ").Value;
        await Save(specialty);

        await using var reader = _database.CreateContext();
        var stored = await reader.Specialties.AsNoTracking().SingleAsync(row => row.Id == specialty.Id);

        Assert.Equal($"طب الأطفال {suffix}", stored.NameAr);
        Assert.Equal($"Pediatrics {suffix}", stored.NameEn);
        Assert.Equal($"طب الاطفال {suffix.ToUpperInvariant()}", stored.NormalizedNameAr);
        Assert.Equal($"PEDIATRICS {suffix.ToUpperInvariant()}", stored.NormalizedNameEn);
        Assert.Equal(SqlServerIntegrationFixture.InitialUtc, stored.CreatedAtUtc);
        Assert.Null(stored.LastModifiedAtUtc);
    }

    [SqlServerTheory]
    [InlineData("أطفال", "اطفال")]
    [InlineData("إطفال", "اطفال")]
    [InlineData("آطفال", "اطفال")]
    [InlineData("ا\u0654طفال", "أطفال")]
    public async Task DuplicateArabic_IsRejectedEvenWhenEnglishDiffers(string firstArabic, string secondArabic)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = Specialty.Create($"{firstArabic} {suffix}", $"Pediatrics {suffix}").Value;
        var second = Specialty.Create($"{secondArabic} {suffix}", $"Paediatrics {suffix}").Value;
        Assert.Equal(first.NormalizedNameAr, second.NormalizedNameAr);
        Assert.NotEqual(first.NormalizedNameEn, second.NormalizedNameEn);
        await Save(first);

        await using (var failedContext = _database.CreateContext())
        {
            failedContext.Specialties.Add(second);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => failedContext.SaveChangesAsync());
            AssertUniqueViolation(error, "IX_Specialties_NormalizedNameAr");
            await Assert.ThrowsAsync<InvalidOperationException>(() => failedContext.SaveChangesAsync());
        }

        await using var reader = _database.CreateContext();
        Assert.Equal(1, await reader.Specialties.CountAsync(row => row.NormalizedNameAr == first.NormalizedNameAr));
        Assert.False(await reader.Specialties.AnyAsync(row => row.Id == second.Id));
    }

    [SqlServerTheory]
    [InlineData("Pediatrics", "pediatrics")]
    [InlineData("Cardiology", "CARDIOLOGY")]
    public async Task DuplicateEnglish_IsRejectedEvenWhenArabicDiffers(string firstEnglish, string secondEnglish)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = Specialty.Create($"التخصص الأول {suffix}", $"{firstEnglish} {suffix}").Value;
        var second = Specialty.Create($"التخصص الثاني {suffix}", $"{secondEnglish} {suffix}").Value;
        Assert.NotEqual(first.NormalizedNameAr, second.NormalizedNameAr);
        Assert.Equal(first.NormalizedNameEn, second.NormalizedNameEn);
        await Save(first);

        await using var failedContext = _database.CreateContext();
        failedContext.Specialties.Add(second);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => failedContext.SaveChangesAsync());
        AssertUniqueViolation(error, "IX_Specialties_NormalizedNameEn");
    }

    [SqlServerFact]
    public async Task DistinctArabicKeys_RemainDistinctInSqlServer()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = Specialty.Create($"جلدية {suffix}", $"Dermatology A {suffix}").Value;
        var second = Specialty.Create($"جلديه {suffix}", $"Dermatology B {suffix}").Value;
        await Save(first);
        await Save(second);

        await using var reader = _database.CreateContext();
        Assert.Equal(2, await reader.Specialties.CountAsync(row => row.Id == first.Id || row.Id == second.Id));
    }

    [SqlServerTheory]
    [InlineData("NameAr")]
    [InlineData("NameEn")]
    [InlineData("NormalizedNameAr")]
    [InlineData("NormalizedNameEn")]
    public async Task Database_RejectsOverlengthNameEvenWhenDomainIsBypassed(string field)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => InsertInvalidRow(field, missing: false));
        Assert.True(error.Number is 2628 or 8152, $"Unexpected SQL error: {error.Number}");
    }

    [SqlServerTheory]
    [InlineData("NameAr")]
    [InlineData("NameEn")]
    [InlineData("NormalizedNameAr")]
    [InlineData("NormalizedNameEn")]
    public async Task Database_RejectsNullNameEvenWhenDomainIsBypassed(string field)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => InsertInvalidRow(field, missing: true));
        Assert.Equal(515, error.Number);
    }

    [SqlServerFact]
    public async Task Update_ProtectsCreationTime_AndPersistsModificationTime()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var specialty = Specialty.Create($"توقيت {suffix}", $"Audit {suffix}").Value;
        await Save(specialty);
        _database.Clock.UtcNow = SqlServerIntegrationFixture.InitialUtc.AddHours(1);

        await using (var writer = _database.CreateContext())
        {
            var tracked = await writer.Specialties.SingleAsync(row => row.Id == specialty.Id);
            tracked.CreatedAtUtc = SqlServerIntegrationFixture.InitialUtc.AddDays(-1);
            tracked.LastModifiedAtUtc = SqlServerIntegrationFixture.InitialUtc.AddDays(-1);
            await writer.SaveChangesAsync();
        }

        await using var reader = _database.CreateContext();
        var stored = await reader.Specialties.AsNoTracking().SingleAsync(row => row.Id == specialty.Id);
        Assert.Equal(SqlServerIntegrationFixture.InitialUtc, stored.CreatedAtUtc);
        Assert.Equal(_database.Clock.UtcNow, stored.LastModifiedAtUtc);
    }

    [SqlServerFact]
    public async Task ConcurrentArabicDuplicates_CommitExactlyOneRow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = Specialty.Create($"أطفال {suffix}", $"First {suffix}").Value;
        var second = Specialty.Create($"اطفال {suffix}", $"Second {suffix}").Value;
        await using var firstContext = _database.CreateContext();
        await using var secondContext = _database.CreateContext();
        firstContext.Specialties.Add(first);
        secondContext.Specialties.Add(second);

        async Task<bool> TrySave(ClinIQ.Infrastructure.Data.AppDbContext context)
        {
            try { await context.SaveChangesAsync(); return true; }
            catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
            {
                AssertUniqueViolation(error, "IX_Specialties_NormalizedNameAr");
                return false;
            }
        }

        var committed = await Task.WhenAll(TrySave(firstContext), TrySave(secondContext));
        Assert.Equal(1, committed.Count(success => success));
        await using var reader = _database.CreateContext();
        Assert.Equal(1, await reader.Specialties.CountAsync(row => row.NormalizedNameAr == first.NormalizedNameAr));
    }

    private async Task Save(Specialty specialty)
    {
        await using var context = _database.CreateContext();
        context.Specialties.Add(specialty);
        await context.SaveChangesAsync();
    }

    private async Task InsertInvalidRow(string field, bool missing)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var values = new Dictionary<string, string?>
        {
            ["NameAr"] = $"عربي {suffix}",
            ["NameEn"] = $"English {suffix}",
            ["NormalizedNameAr"] = $"عربي {suffix}",
            ["NormalizedNameEn"] = $"ENGLISH {suffix}"
        };
        values[field] = missing ? null : new string('x', Specialty.NameMaxLength + 1);
        await using var context = _database.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Specialties]
                ([Id], [NameAr], [NameEn], [NormalizedNameAr], [NormalizedNameEn], [CreatedAtUtc])
            VALUES ({Guid.NewGuid()}, {values["NameAr"]}, {values["NameEn"]},
                {values["NormalizedNameAr"]}, {values["NormalizedNameEn"]}, {_database.Clock.UtcNow})
            """);
    }

    private static void AssertUniqueViolation(DbUpdateException error, string expectedIndex)
    {
        var sqlError = Assert.IsType<SqlException>(error.InnerException);
        Assert.True(sqlError.Number is 2601 or 2627, $"Unexpected SQL error: {sqlError.Number}");
        Assert.Contains(expectedIndex, sqlError.Message);
    }
}
