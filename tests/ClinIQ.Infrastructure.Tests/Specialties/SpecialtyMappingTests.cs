using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Domain.Common;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Specialties;

public sealed class SpecialtyMappingTests
{
    [Fact]
    public void SpecialtyVersionIsGeneratedBySqlServerForConcurrency()
    {
        using var context = CreateContext();
        var property = GetSpecialtyModel(context).FindProperty(nameof(Specialty.Version))!;
        Assert.True(property.IsConcurrencyToken);
        Assert.Equal("rowversion", property.GetColumnType());
    }

    [Fact]
    public void HistoricalChangeRetainsActorForeignKeyWithoutBlockingSpecialtyDeletion()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(SpecialtyCatalogChange))!;
        var actor = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(nameof(SpecialtyCatalogChange.ActorUserId), Assert.Single(actor.Properties).Name);
        Assert.Equal(DeleteBehavior.Restrict, actor.DeleteBehavior);
        Assert.DoesNotContain(entity.GetForeignKeys(), key => key.Properties.Any(property => property.Name == nameof(SpecialtyCatalogChange.SpecialtyId)));
    }

    [Theory]
    [InlineData(nameof(Specialty.NameAr))]
    [InlineData(nameof(Specialty.NameEn))]
    public void Model_MapsRequiredUnicodeDisplayNamesWithoutOverridingCollation(string propertyName)
    {
        using var context = CreateContext();
        var entity = GetSpecialtyModel(context);
        var property = entity.FindProperty(propertyName)!;

        Assert.Equal("Specialties", entity.GetTableName());
        Assert.False(property.IsNullable);
        Assert.True(property.IsUnicode());
        Assert.Equal(Specialty.NameMaxLength, property.GetMaxLength());
        Assert.Null(property.GetCollation());
    }

    [Theory]
    [InlineData(nameof(Specialty.NormalizedNameAr))]
    [InlineData(nameof(Specialty.NormalizedNameEn))]
    public void Model_RequiresEachNormalizedNameToBeUniqueWithExactComparison(string propertyName)
    {
        using var context = CreateContext();
        var entity = GetSpecialtyModel(context);
        var property = entity.FindProperty(propertyName)!;

        Assert.False(property.IsNullable);
        Assert.True(property.IsUnicode());
        Assert.Equal(Specialty.NameMaxLength, property.GetMaxLength());
        Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Count == 1 && index.Properties[0].Name == propertyName);
    }

    [Fact]
    public void Model_UsesNormalizedKeysInsteadOfRawDisplayNamesForUniqueness()
    {
        using var context = CreateContext();
        var entity = GetSpecialtyModel(context);

        Assert.DoesNotContain(entity.GetIndexes(), index => index.Properties.Any(property =>
            property.Name is nameof(Specialty.NameAr) or nameof(Specialty.NameEn)));
    }

    [Fact]
    public void Model_ExcludesDomainEventsFromPersistence()
    {
        using var context = CreateContext();
        var entity = GetSpecialtyModel(context);

        Assert.Null(entity.FindProperty(nameof(Entity.DomainEvents)));
        Assert.Null(entity.FindNavigation(nameof(Entity.DomainEvents)));
    }

    [Fact]
    public void Tracking_PreservesFactoryIdAndExposesSameSetThroughInterface()
    {
        using var context = CreateContext();
        var specialty = Specialty.Create("طب الأطفال", "Pediatrics").Value;
        var originalId = specialty.Id;

        IAppDbContext abstraction = context;
        var entry = abstraction.Specialties.Add(specialty);

        Assert.Same(context.Specialties, abstraction.Specialties);
        Assert.Equal(originalId, specialty.Id);
        Assert.False(entry.Property(entity => entity.Id).IsTemporary);
        Assert.Equal(ValueGenerated.Never, entry.Property(entity => entity.Id).Metadata.ValueGenerated);
        Assert.Equal(EntityState.Added, entry.State);
        Assert.Equal("طب الاطفال", entry.Property(entity => entity.NormalizedNameAr).CurrentValue);
        Assert.Equal("PEDIATRICS", entry.Property(entity => entity.NormalizedNameEn).CurrentValue);
    }

    [Fact]
    public void GenerateCreateScript_IncludesSpecialtiesAlongsideIdentity()
    {
        using var context = CreateContext();

        var sql = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [Specialties]", sql);
        Assert.Contains("CREATE TABLE [AspNetUsers]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Specialties_NormalizedNameAr]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Specialties_NormalizedNameEn]", sql);
        Assert.DoesNotContain("Arabic_100_CI_AS", sql);
        Assert.DoesNotContain("[DomainEvents]", sql);
    }

    private static IEntityType GetSpecialtyModel(AppDbContext context) =>
        context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Specialty))!;

    private static AppDbContext CreateContext()
    {
        // Model/query inspection only: these tests never connect or call SaveChanges.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.;Database=NotUsed;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new AppDbContext(options, new NoOpPublisher());
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification,
            CancellationToken cancellationToken = default) where TNotification : INotification =>
            Task.CompletedTask;
    }
}
