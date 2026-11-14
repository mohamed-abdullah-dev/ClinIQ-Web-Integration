using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Application.Features.Specialties;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SpecialtyAdministrationTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    private Task<T> Admin<T>(Func<ISpecialtyAdministrationService, Task<T>> work) =>
        fixture.AsAsync(fixture.Admin.Id, provider => work(provider.GetRequiredService<ISpecialtyAdministrationService>()));
    private Task<Result<AdminSpecialtyDto>> Create(string? ar = null, string? en = null) =>
        Admin(service => service.CreateAsync(ar ?? $"اختبار {Guid.NewGuid():N}", en ?? $"Synthetic {Guid.NewGuid():N}", default));

    [SqlServerFact]
    public async Task CreateRenameAndDeleteRetainTrustedTransactionalHistory()
    {
        var first = (await Create()).Value;
        var renamed = (await Admin(service => service.RenameAsync(first.Id, "أطفال اختبار", "Synthetic pediatrics", first.Version, default))).Value;
        Assert.Equal(first.Id, renamed.Id); Assert.NotEqual(first.Version, renamed.Version);
        Assert.Equal(renamed, (await Admin(service => service.GetAsync(first.Id, default))).Value);
        Assert.Contains((await Admin(service => service.ListAsync(default))).Value, row => row.Id == first.Id);
        Assert.True((await Admin(service => service.DeleteAsync(first.Id, renamed.Version, default))).IsSuccess);
        Assert.Equal("Specialty.NotFound", (await Admin(service => service.GetAsync(first.Id, default))).Errors.Single().Code);
        await using var db = fixture.Database.CreateContext();
        var history = await db.SpecialtyCatalogChanges.Where(x => x.SpecialtyId == first.Id).ToArrayAsync();
        Assert.Equal(3, history.Length);
        Assert.All(history, change => { Assert.Equal(fixture.Admin.Id, change.ActorUserId); Assert.Equal(fixture.Database.Clock.GetUtcNow(), change.OccurredAtUtc); });
        Assert.Equal(first.NameEn, history.Single(x => x.Operation == SpecialtyCatalogOperation.Renamed).PreviousNameEn);
        Assert.Null(history.Single(x => x.Operation == SpecialtyCatalogOperation.Deleted).NameEn);
        Assert.False(await db.Specialties.AnyAsync(x => x.Id == first.Id));
    }

    [SqlServerTheory]
    [InlineData("اطفال", "Different English", "Specialty.NameArTaken")]
    [InlineData("تخصص مختلف", "pEdIaTrIcS", "Specialty.NameEnTaken")]
    public async Task EitherNormalizedNameIndependentlyPreventsDuplicates(string ar, string en, string code)
    {
        var suffix = Guid.NewGuid().ToString("N");
        Assert.True((await Create("أطفال " + suffix, "Pediatrics " + suffix)).IsSuccess);
        var result = await Create(ar + " " + suffix, en + " " + suffix);
        Assert.Equal(code, result.Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task ConcurrentSameNameCreationHasOneWinnerAndOneAuditEntry()
    {
        var name = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Create("تزامن " + name, "Race " + name), Create("تزامن " + name, "RACE " + name));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Contains(results.Single(result => result.IsError).Errors.Single().Code, new[] { "Specialty.NameArTaken", "Specialty.NameEnTaken" });
        var id = results.Single(result => result.IsSuccess).Value.Id;
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.SpecialtyCatalogChanges.CountAsync(x => x.SpecialtyId == id));
    }

    [SqlServerFact]
    public async Task StaleConcurrentRenamesCannotSilentlyOverwriteOneAnother()
    {
        var row = (await Create()).Value; var suffix = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(
            Admin(service => service.RenameAsync(row.Id, "أول " + suffix, "First " + suffix, row.Version, default)),
            Admin(service => service.RenameAsync(row.Id, "ثاني " + suffix, "Second " + suffix, row.Version, default)));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("Specialty.StaleVersion", results.Single(result => result.IsError).Errors.Single().Code);
        Assert.Equal("Specialty.StaleVersion", (await Admin(service => service.DeleteAsync(row.Id, row.Version, default))).Errors.Single().Code);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(2, await db.SpecialtyCatalogChanges.CountAsync(x => x.SpecialtyId == row.Id));
    }

    [SqlServerFact]
    public async Task RenameToAnotherSpecialtyNameLeavesDataAndAuditUnchanged()
    {
        var first = (await Create()).Value; var second = (await Create()).Value;
        Assert.True((await Admin(service => service.RenameAsync(first.Id, second.NameAr, first.NameEn, first.Version, default))).IsError);
        Assert.Equal(first, (await Admin(service => service.GetAsync(first.Id, default))).Value);
        await using var db = fixture.Database.CreateContext(); Assert.Equal(1, await db.SpecialtyCatalogChanges.CountAsync(x => x.SpecialtyId == first.Id));
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedSpecialtyCannotBeDeletedButCanBeRenamedWithoutChangingDoctorLinks(bool approved)
    {
        var doctor = await fixture.DoctorAsync();
        if (approved)
            await fixture.AsAsync(fixture.Admin.Id, async provider =>
            {
                var reviews = provider.GetRequiredService<IDoctorReviewService>();
                var detail = (await reviews.GetAsync(doctor.Receipt.RegistrationId, default)).Value;
                Assert.True((await reviews.ReviewAsync(detail.Registration.Id, true, "Synthetic approval", detail.Version, default)).IsSuccess);
                return true;
            });
        var row = (await Admin(service => service.GetAsync(fixture.SpecialtyId, default))).Value;
        Assert.Equal("Specialty.InUse", (await Admin(service => service.DeleteAsync(row.Id, row.Version, default))).Errors.Single().Code);
        var suffix = Guid.NewGuid().ToString("N");
        var renamed = await Admin(service => service.RenameAsync(row.Id, "تخصص مرتبط " + suffix, "Linked " + suffix, row.Version, default));
        Assert.True(renamed.IsSuccess); Assert.Equal(row.Id, renamed.Value.Id);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(row.Id, (await db.DoctorRegistrations.SingleAsync(x => x.Id == doctor.Receipt.RegistrationId)).SpecialtyId);
        if (approved) Assert.Equal(row.Id, (await db.DoctorProfiles.SingleAsync(x => x.DoctorRegistrationId == doctor.Receipt.RegistrationId)).SpecialtyId);
    }

    [SqlServerTheory]
    [InlineData(null, true)]
    [InlineData("Patient", true)]
    [InlineData("Doctor", true)]
    [InlineData("Admin", false)]
    public async Task TheServiceRequiresAConfirmedAdmin(string? role, bool confirmed)
    {
        var user = await fixture.UserAsync(role, confirmed);
        await fixture.AsAsync(user.Id, async provider =>
        {
            var service = provider.GetRequiredService<ISpecialtyAdministrationService>();
            Assert.Equal("Specialty.Forbidden", (await service.ListAsync(default)).Errors.Single().Code);
            Assert.Equal("Specialty.Forbidden", (await service.CreateAsync("غير مسموح", "Forbidden", default)).Errors.Single().Code);
            Assert.Equal("Specialty.Forbidden", (await service.DeleteAsync(fixture.SpecialtyId, "AAAAAAAAAAA=", default)).Errors.Single().Code);
            return true;
        });
    }

    [SqlServerFact]
    public async Task AnonymousServiceCallsAndMissingTargetsReturnExpectedErrors()
    {
        await fixture.AsAsync(null, async provider =>
        {
            Assert.Equal("Specialty.Unauthorized", (await provider.GetRequiredService<ISpecialtyAdministrationService>().ListAsync(default)).Errors.Single().Code);
            return true;
        });
        Assert.Equal("Specialty.NotFound", (await Admin(service => service.DeleteAsync(Guid.NewGuid(), "AAAAAAAAAAA=", default))).Errors.Single().Code);
    }

    [SqlServerTheory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task AuditFailureRollsBackTheCatalogMutation(string operation)
    {
        var row = (await Create()).Value;
        await using var db = fixture.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER TR_SyntheticCatalogAuditFailure ON SpecialtyCatalogChanges INSTEAD OF INSERT AS
            BEGIN THROW 51000, 'Synthetic audit failure.', 1; END;
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                if (operation == "create") await Create("فشل إنشاء", "Synthetic failed creation");
                else if (operation == "rename") await Admin(service => service.RenameAsync(row.Id, "فشل تعديل", "Synthetic failed rename", row.Version, default));
                else await Admin(service => service.DeleteAsync(row.Id, row.Version, default));
            });
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_SyntheticCatalogAuditFailure"); }
        Assert.Equal(row, (await Admin(service => service.GetAsync(row.Id, default))).Value);
        Assert.False(await db.Specialties.AnyAsync(x => x.NameEn == "Synthetic failed creation"));
        Assert.Equal(1, await db.SpecialtyCatalogChanges.CountAsync(x => x.SpecialtyId == row.Id));
    }

    [SqlServerFact]
    public async Task MigrationAssignsRowVersionsToExistingCatalogWithoutChangingOldFields()
    {
        await using var database = new AsyncDatabase();
        await database.Database.InitializeAsync();
        await using var db = database.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261027121356_PatientProfile");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Specialties (Id,NameAr,NameEn,NormalizedNameAr,NormalizedNameEn,CreatedAtUtc)
            VALUES ('4DCE8424-DB03-4317-874F-0BBA35D191B1',N'أطفال',N'Pediatrics',N'اطفال',N'PEDIATRICS','2026-10-25T12:00:00+00:00');
            """);
        await migrator.MigrateAsync();
        var row = await db.Specialties.AsNoTracking().SingleAsync();
        Assert.Equal("أطفال", row.NameAr); Assert.Equal("Pediatrics", row.NameEn); Assert.Equal("اطفال", row.NormalizedNameAr);
        Assert.Equal(8, row.Version.Length); Assert.Null(row.CreatedByUserId);
        Assert.Empty(await db.SpecialtyCatalogChanges.ToArrayAsync());
    }

    private sealed class AsyncDatabase : IAsyncDisposable
    {
        public SqlServerIntegrationFixture Database { get; } = new();
        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }
}
