using ClinIQ.Application.Features.Specialties.Queries.GetSpecialties;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SpecialtyQueryTests : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [SqlServerFact]
    public async Task EmptyCatalog_ReturnsSuccessfulEmptyList()
    {
        await using var context = _database.CreateContext();

        var result = await new GetSpecialtiesQueryHandler(context).Handle(new GetSpecialtiesQuery(), default);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task SeededCatalog_ReturnsDisplayDtosInStableOrderWithoutTracking()
    {
        await using (var writer = _database.CreateContext())
            Assert.True((await new SpecialtySeeder(writer).SeedAsync()).IsSuccess);
        await using var reader = _database.CreateContext();
        var handler = new GetSpecialtiesQueryHandler(reader);

        var first = await handler.Handle(new GetSpecialtiesQuery(), default);
        var second = await handler.Handle(new GetSpecialtiesQuery(), default);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(new[] { "Cardiology", "Dentistry", "Dermatology", "General Surgery", "Internal Medicine",
            "Obstetrics and Gynecology", "Ophthalmology", "Orthopedics", "Otolaryngology", "Pediatrics" },
            first.Value.Select(row => row.NameEn));
        Assert.Equal("القلب", first.Value[0].NameAr);
        Assert.Equal("طب الأطفال", first.Value[^1].NameAr);
        Assert.All(first.Value, row => Assert.NotEqual(Guid.Empty, row.Id));
        Assert.Equal(10, first.Value.Select(row => row.Id).Distinct().Count());
        Assert.Equal(first.Value.ToArray(), second.Value.ToArray());
        Assert.Empty(reader.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task Query_SortsMixedCaseNames_AndReadsLaterDatabaseChanges()
    {
        await using (var writer = _database.CreateContext())
        {
            writer.Specialties.AddRange(
                Specialty.Create("التخصص الثالث", "zeta").Value,
                Specialty.Create("التخصص الثاني", "Beta").Value,
                Specialty.Create("التخصص الأول", "alpha").Value);
            await writer.SaveChangesAsync();
        }
        await using var reader = _database.CreateContext();
        var handler = new GetSpecialtiesQueryHandler(reader);
        var first = await handler.Handle(new GetSpecialtiesQuery(), default);
        Assert.Equal(new[] { "alpha", "Beta", "zeta" }, first.Value.Select(row => row.NameEn));

        await using (var writer = _database.CreateContext())
        {
            writer.Specialties.Add(Specialty.Create("تخصص جديد", "aardvark").Value);
            await writer.SaveChangesAsync();
        }
        var second = await handler.Handle(new GetSpecialtiesQuery(), default);

        Assert.Equal(new[] { "aardvark", "alpha", "Beta", "zeta" }, second.Value.Select(row => row.NameEn));
        Assert.Empty(reader.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task CanceledQuery_PropagatesCancellation()
    {
        await using var context = _database.CreateContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetSpecialtiesQueryHandler(context).Handle(
                new GetSpecialtiesQuery(), new CancellationToken(canceled: true)));
    }
}
