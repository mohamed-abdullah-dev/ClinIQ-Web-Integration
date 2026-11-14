using ClinIQ.Domain.Specialties;
using Xunit;

namespace ClinIQ.Domain.Tests.Specialties;

public sealed class SpecialtyAdministrationTests
{
    [Fact]
    public void RenamePreservesIdentityAndRebuildsBothComparisonKeys()
    {
        var row = Specialty.Create("جراحة", "Surgery").Value; var id = row.Id;
        Assert.True(row.Rename("  أطفال  ", " Pediatrics ").IsSuccess);
        Assert.Equal(id, row.Id); Assert.Equal("أطفال", row.NameAr); Assert.Equal("اطفال", row.NormalizedNameAr);
        Assert.Equal("Pediatrics", row.NameEn); Assert.Equal("PEDIATRICS", row.NormalizedNameEn);
    }

    [Theory]
    [InlineData(null, "Valid")]
    [InlineData("Valid", "")]
    [InlineData("   ", "Valid")]
    public void FailedRenameDoesNotPartiallyMutateAnyName(string? ar, string? en)
    {
        var row = Specialty.Create("جراحة", "Surgery").Value;
        Assert.True(row.Rename(ar, en).IsError);
        Assert.Equal("جراحة", row.NameAr); Assert.Equal("Surgery", row.NameEn);
        Assert.Equal("جراحة", row.NormalizedNameAr); Assert.Equal("SURGERY", row.NormalizedNameEn);
    }

    [Fact]
    public void RenameRetainsTheExistingNameLengthBound()
    {
        var row = Specialty.Create("جراحة", "Surgery").Value;
        Assert.True(row.Rename(new string('ا', Specialty.NameMaxLength + 1), "Valid").IsError);
        Assert.True(row.Rename("Valid", new string('a', Specialty.NameMaxLength + 1)).IsError);
        Assert.Equal("Surgery", row.NameEn);
    }

    [Fact]
    public void AuditSnapshotsKeepTheActorTimeAndDeletedHistoricalIdentity()
    {
        var row = Specialty.Create("جراحة", "Surgery").Value;
        var actor = Guid.NewGuid(); var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var created = SpecialtyCatalogChange.Created(row, actor, now);
        Assert.Null(created.PreviousNameAr); Assert.Equal("Surgery", created.NameEn);
        row.Rename("أطفال", "Pediatrics");
        var renamed = SpecialtyCatalogChange.Renamed(row, "جراحة", "Surgery", actor, now);
        Assert.Equal("Surgery", renamed.PreviousNameEn); Assert.Equal("Pediatrics", renamed.NameEn);
        var deleted = SpecialtyCatalogChange.Deleted(row, actor, now);
        Assert.Equal(row.Id, deleted.SpecialtyId); Assert.Equal(actor, deleted.ActorUserId); Assert.Equal(now, deleted.OccurredAtUtc);
        Assert.Equal("Pediatrics", deleted.PreviousNameEn); Assert.Null(deleted.NameEn);
        Assert.Throws<ArgumentException>(() => SpecialtyCatalogChange.Created(row, Guid.Empty, now));
    }
}
