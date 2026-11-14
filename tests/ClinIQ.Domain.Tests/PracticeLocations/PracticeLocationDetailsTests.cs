using ClinIQ.Domain.PracticeLocations;
using Xunit;

namespace ClinIQ.Domain.Tests.PracticeLocations;

public sealed class PracticeLocationDetailsTests
{
    [Fact]
    public void RequiredPhysicalDetailsAreTrimmedAndOptionalCoordinatesStayAbsent()
    {
        var result = PracticeLocationDetails.Create("  Synthetic place  ", "  Synthetic address  ", null, null);
        Assert.True(result.IsSuccess); Assert.Equal("Synthetic place", result.Value.Name); Assert.Equal("Synthetic address", result.Value.Address);
        Assert.Null(result.Value.Latitude); Assert.Null(result.Value.Longitude);
    }
    [Theory]
    [InlineData("", "Address")]
    [InlineData("Name", " ")]
    [InlineData(null, "Address")]
    [InlineData("Name", null)]
    public void MissingNameOrAddressFails(string? name, string? address) => Assert.True(PracticeLocationDetails.Create(name, address, null, null).IsError);

    [Fact]
    public void TextLimitsAreValidatedAfterTrimming()
    {
        Assert.True(PracticeLocationDetails.Create(new string('N', 200), new string('A', 500), null, null).IsSuccess);
        Assert.True(PracticeLocationDetails.Create(new string('N', 201), "Address", null, null).IsError);
        Assert.True(PracticeLocationDetails.Create("Name", new string('A', 501), null, null).IsError);
    }
    [Theory]
    [InlineData("0", "0")]
    [InlineData("90", "180")]
    [InlineData("-90", "-180")]
    [InlineData("30.0444196", "31.2357116")]
    public void ValidCoordinatePairsIncludingZeroAndBoundariesAreAccepted(string latitude, string longitude) =>
        Assert.True(Create(latitude, longitude).IsSuccess);

    [Theory]
    [InlineData("90.000000001", "0")]
    [InlineData("-90.000000001", "0")]
    [InlineData("0", "180.000000001")]
    [InlineData("0", "-180.000000001")]
    [InlineData("30.1234567891", "31")]
    [InlineData(null, "31")]
    [InlineData("30", null)]
    public void InvalidOrPartialPairsAreRejected(string? latitude, string? longitude) => Assert.True(Create(latitude, longitude).IsError);

    [Theory]
    [InlineData(PracticeLocationEvidenceKind.FacilityLicense, true)]
    [InlineData(PracticeLocationEvidenceKind.WorkAuthorization, true)]
    [InlineData((PracticeLocationEvidenceKind)0, false)]
    [InlineData((PracticeLocationEvidenceKind)99, false)]
    public void FacilityEvidenceUsesItsOwnKinds(PracticeLocationEvidenceKind kind, bool expected) =>
        Assert.Equal(expected, new PracticeLocationEvidenceMetadata(kind, "synthetic.pdf", "synthetic.pdf", "application/pdf", 10, new string('A', 64)).IsValid);

    private static ClinIQ.Domain.Common.Results.Result<PracticeLocationDetails> Create(string? latitude, string? longitude) =>
        PracticeLocationDetails.Create("Place", "Address", Parse(latitude), Parse(longitude));
    private static decimal? Parse(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
