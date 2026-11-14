using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SpecialtySeedTests : IAsyncLifetime
{
    // Each case gets a fresh database so conflict scenarios cannot affect other cases.
    private readonly SqlServerIntegrationFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [SqlServerFact]
    public async Task RepeatedSeed_PreservesIdsAndTimestampsWithoutDuplicates()
    {
        var first = await Seed();
        Assert.Equal(new SpecialtySeedSummary(10, 0), first);
        await using var firstReader = _database.CreateContext();
        var before = await firstReader.Specialties.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);

        var second = await Seed();
        Assert.Equal(new SpecialtySeedSummary(0, 10), second);
        await using var secondReader = _database.CreateContext();
        var after = await secondReader.Specialties.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();

        Assert.Equal(10, after.Length);
        Assert.Equal(before.Select(Snapshot), after.Select(Snapshot));
        Assert.All(after, row => Assert.Null(row.LastModifiedAtUtc));
        Assert.Contains(after, row => row.NameAr == "طب الأطفال" && row.NameEn == "Pediatrics");
        Assert.Contains(after, row => row.NameAr == "طب الأسنان" && row.NameEn == "Dentistry");
    }

    [SqlServerFact]
    public async Task PartialCatalog_PreservesExistingDisplayAndAudit_AndAddsMissingEntries()
    {
        var existing = Specialty.Create("طب الاطفال", "pediatrics").Value;
        await Save(existing);
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);

        var summary = await Seed();

        Assert.Equal(new SpecialtySeedSummary(9, 1), summary);
        await using var reader = _database.CreateContext();
        var stored = await reader.Specialties.AsNoTracking().SingleAsync(row => row.Id == existing.Id);
        Assert.Equal("طب الاطفال", stored.NameAr);
        Assert.Equal("pediatrics", stored.NameEn);
        Assert.Equal(SqlServerIntegrationFixture.InitialUtc, stored.CreatedAtUtc);
        Assert.Null(stored.LastModifiedAtUtc);
        Assert.Equal(10, await reader.Specialties.CountAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MatchingOnlyOneLanguage_ReturnsConflictWithoutOverwriting(bool arabicMatches)
    {
        var existing = arabicMatches
            ? Specialty.Create("طب الأطفال", "Paediatrics").Value
            : Specialty.Create("تخصص مختلف", "Pediatrics").Value;
        await Save(existing);
        await using var context = _database.CreateContext();

        var result = await new SpecialtySeeder(context).SeedAsync();

        Assert.True(result.IsError);
        Assert.Equal("SpecialtySeed.NameConflict", Assert.Single(result.Errors).Code);
        await using var reader = _database.CreateContext();
        var stored = Assert.Single(await reader.Specialties.AsNoTracking().ToArrayAsync());
        Assert.Equal(Snapshot(existing), Snapshot(stored));
    }

    [SqlServerFact]
    public async Task KeysPointingToDifferentRows_ReturnConflictWithoutMerging()
    {
        await Save(Specialty.Create("طب الأطفال", "Other Name").Value);
        await Save(Specialty.Create("تخصص مختلف", "Pediatrics").Value);
        await using var context = _database.CreateContext();

        var result = await new SpecialtySeeder(context).SeedAsync();

        Assert.True(result.IsError);
        Assert.Equal("SpecialtySeed.NameConflict", Assert.Single(result.Errors).Code);
        await using var reader = _database.CreateContext();
        Assert.Equal(2, await reader.Specialties.CountAsync());
    }

    [SqlServerFact]
    public async Task ConflictAtLastCatalogEntry_DoesNotInsertEarlierEntries()
    {
        await Save(Specialty.Create("طب الأسنان", "Other Dentistry").Value);
        await using var context = _database.CreateContext();

        var result = await new SpecialtySeeder(context).SeedAsync();

        Assert.True(result.IsError);
        await using var reader = _database.CreateContext();
        var stored = Assert.Single(await reader.Specialties.AsNoTracking().ToArrayAsync());
        Assert.Equal("Other Dentistry", stored.NameEn);
        Assert.False(await reader.Specialties.AnyAsync(row => row.NameEn == "Pediatrics"));
    }

    [SqlServerFact]
    public async Task ConcurrentSeedRuns_AddCatalogOnce_AndBothSucceed()
    {
        await using var firstContext = _database.CreateContext();
        await using var secondContext = _database.CreateContext();

        var results = await Task.WhenAll(
            new SpecialtySeeder(firstContext).SeedAsync(),
            new SpecialtySeeder(secondContext).SeedAsync());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(new[] { 0, 10 }, results.Select(result => result.Value.Added).OrderBy(value => value));
        Assert.Equal(new[] { 0, 10 }, results.Select(result => result.Value.Existing).OrderBy(value => value));
        await using var reader = _database.CreateContext();
        Assert.Equal(10, await reader.Specialties.CountAsync());
    }

    [SqlServerFact]
    public async Task AlreadyCanceledSeed_WritesNothing()
    {
        await using var context = _database.CreateContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SpecialtySeeder(context).SeedAsync(new CancellationToken(canceled: true)));

        await using var reader = _database.CreateContext();
        Assert.Equal(0, await reader.Specialties.CountAsync());
    }

    private async Task<SpecialtySeedSummary> Seed()
    {
        await using var context = _database.CreateContext();
        var result = await new SpecialtySeeder(context).SeedAsync();
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task Save(Specialty specialty)
    {
        await using var context = _database.CreateContext();
        context.Specialties.Add(specialty);
        await context.SaveChangesAsync();
    }

    private static object Snapshot(Specialty row) => new
    {
        row.Id, row.NameAr, row.NameEn, row.NormalizedNameAr, row.NormalizedNameEn,
        row.CreatedAtUtc, row.LastModifiedAtUtc
    };
}
