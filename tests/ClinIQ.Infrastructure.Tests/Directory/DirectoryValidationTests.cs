using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Directory.Queries;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.PublicDirectory;

public sealed class DirectoryValidationTests
{
    [Fact]
    public void OptionalFiltersAndExplicitCoordinatePairsAreAccepted()
    {
        var validator = new DirectoryFilterValidator();
        Assert.True(validator.Validate(new DirectoryFilter()).IsValid);
        Assert.True(validator.Validate(new DirectoryFilter() { Latitude = 0, Longitude = 0, MaxDistanceKm = 1, Sort = DirectorySort.Distance }).IsValid);
        Assert.True(validator.Validate(new DirectoryFilter() { MinFee = 0, MaxFee = 123.45m, Page = 100000, PageSize = 100 }).IsValid);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(100001, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public void PaginationIsBounded(int page, int pageSize) => Assert.False(new DirectoryFilterValidator().Validate(new DirectoryFilter() { Page = page, PageSize = pageSize }).IsValid);

    [Fact]
    public void InvalidBusinessAndGeographicFiltersAreRejected()
    {
        var invalid = new DirectoryFilter[]
        {
            new() { SpecialtyId = Guid.Empty }, new() { Name = new string('a', 101) }, new() { Area = new string('a', 101) },
            new() { MinFee = -1 }, new() { MaxFee = -1 }, new() { MinFee = 10, MaxFee = 9 }, new() { MaxFee = 1.001m },
            new() { Sort = (DirectorySort)99 }, new() { Sort = DirectorySort.Distance }, new() { Latitude = 30 },
            new() { Longitude = 31 }, new() { Latitude = 91, Longitude = 0 }, new() { Latitude = 0, Longitude = -181 },
            new() { MaxDistanceKm = 1 }, new() { Latitude = 0, Longitude = 0, MaxDistanceKm = double.NaN },
            new() { Latitude = 0, Longitude = 0, MaxDistanceKm = double.PositiveInfinity },
            new() { Latitude = 0, Longitude = 0, MaxDistanceKm = 0 }, new() { Latitude = 0, Longitude = 0, MaxDistanceKm = 1001 }
        };
        foreach (var filter in invalid) Assert.False(new DirectoryFilterValidator().Validate(filter).IsValid);
    }

    [Fact]
    public void ProfileQueriesRequireRealIdsAndAValidWindow()
    {
        Assert.False(new PublicDoctorValidator().Validate(new PublicDoctorQuery(Guid.Empty, new())).IsValid);
        Assert.False(new PublicLocationValidator().Validate(new PublicLocationQuery(Guid.NewGuid(), new() { PageSize = 101 })).IsValid);
        Assert.False(new PublicClinicValidator().Validate(new PublicClinicQuery(Guid.Empty, new())).IsValid);
        Assert.False(new PublicBranchValidator().Validate(new PublicBranchQuery(Guid.NewGuid(), Guid.Empty, new())).IsValid);
        Assert.False(new PublicSessionValidator().Validate(new PublicSessionQuery(Guid.Empty)).IsValid);
    }
}
