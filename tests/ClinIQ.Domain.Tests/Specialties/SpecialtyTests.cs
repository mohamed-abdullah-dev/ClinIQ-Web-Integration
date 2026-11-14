using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Specialties;
using Xunit;

namespace ClinIQ.Domain.Tests.Specialties;

public sealed class SpecialtyTests
{
    [Fact]
    public void Create_WithValidNames_TrimsOuterWhitespaceAndCreatesId()
    {
        var result = Specialty.Create("  طب الأطفال  ", "  Pediatrics  ");

        Assert.True(result.IsSuccess);
        Assert.Equal("طب الأطفال", result.Value.NameAr);
        Assert.Equal("Pediatrics", result.Value.NameEn);
        Assert.NotEqual(Guid.Empty, result.Value.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Create_WithMissingArabicName_ReturnsFieldError(string? nameAr)
    {
        var result = Specialty.Create(nameAr, "Pediatrics");

        var error = Assert.Single(result.Errors);
        Assert.Equal(SpecialtyErrors.NameArRequired, error);
        Assert.Equal(nameof(Specialty.NameAr), error.Field);
        Assert.Equal(ErrorType.Validation, error.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Create_WithMissingEnglishName_ReturnsFieldError(string? nameEn)
    {
        var result = Specialty.Create("طب الأطفال", nameEn);

        var error = Assert.Single(result.Errors);
        Assert.Equal(SpecialtyErrors.NameEnRequired, error);
        Assert.Equal(nameof(Specialty.NameEn), error.Field);
        Assert.Equal(ErrorType.Validation, error.Type);
    }

    [Fact]
    public void Create_WithBothNamesMissing_ReturnsBothErrors()
    {
        var result = Specialty.Create(null, " ");

        Assert.True(result.IsError);
        Assert.Collection(result.Errors,
            error => Assert.Equal(SpecialtyErrors.NameArRequired, error),
            error => Assert.Equal(SpecialtyErrors.NameEnRequired, error));
    }

    [Fact]
    public void Create_WithNamesAtMaximumLength_SucceedsAfterTrimming()
    {
        var nameAr = new string('س', Specialty.NameMaxLength);
        var nameEn = new string('a', Specialty.NameMaxLength);

        var result = Specialty.Create($" {nameAr} ", $" {nameEn} ");

        Assert.True(result.IsSuccess);
        Assert.Equal(nameAr, result.Value.NameAr);
        Assert.Equal(nameEn, result.Value.NameEn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_WithOneNameOverMaximumLength_ReturnsCorrectError(bool arabic)
    {
        var nameAr = arabic ? new string('س', Specialty.NameMaxLength + 1) : "طب الأطفال";
        var nameEn = arabic ? "Pediatrics" : new string('a', Specialty.NameMaxLength + 1);

        var result = Specialty.Create(nameAr, nameEn);

        var error = Assert.Single(result.Errors);
        Assert.Equal(arabic ? SpecialtyErrors.NameArTooLong : SpecialtyErrors.NameEnTooLong, error);
    }

    [Fact]
    public void Create_WithBothNamesOverMaximumLength_ReturnsBothErrors()
    {
        var result = Specialty.Create(
            new string('س', Specialty.NameMaxLength + 1),
            new string('a', Specialty.NameMaxLength + 1));

        Assert.True(result.IsError);
        Assert.Collection(result.Errors,
            error => Assert.Equal(SpecialtyErrors.NameArTooLong, error),
            error => Assert.Equal(SpecialtyErrors.NameEnTooLong, error));
    }

    [Fact]
    public void Create_PreservesSpellingCaseAndInternalSpaces()
    {
        var result = Specialty.Create("الأنف  والأذن", "Ear  Nose and Throat");

        Assert.True(result.IsSuccess);
        Assert.Equal("الأنف  والأذن", result.Value.NameAr);
        Assert.Equal("Ear  Nose and Throat", result.Value.NameEn);
    }

    [Theory]
    [InlineData("أطفال")]
    [InlineData("إطفال")]
    [InlineData("آطفال")]
    [InlineData("ا\u0654طفال")]
    [InlineData("ا\u0655طفال")]
    [InlineData("ا\u0653طفال")]
    public void Create_WithAlefVariants_UsesSameArabicKeyEvenWhenEnglishDiffers(string nameAr)
    {
        var first = Specialty.Create(nameAr, "Pediatrics").Value;
        var second = Specialty.Create("اطفال", "Paediatrics").Value;

        Assert.Equal("اطفال", first.NormalizedNameAr);
        Assert.Equal(second.NormalizedNameAr, first.NormalizedNameAr);
        Assert.NotEqual(second.NormalizedNameEn, first.NormalizedNameEn);
        Assert.Equal(nameAr, first.NameAr);
    }

    [Theory]
    [InlineData("Pediatrics", "pediatrics")]
    [InlineData("PEDIATRICS", "Pediatrics")]
    public void Create_WithEnglishCaseVariants_UsesSameEnglishKeyEvenWhenArabicDiffers(
        string firstEnglish, string secondEnglish)
    {
        var first = Specialty.Create("أطفال", firstEnglish).Value;
        var second = Specialty.Create("طب الأطفال", secondEnglish).Value;

        Assert.Equal("PEDIATRICS", first.NormalizedNameEn);
        Assert.Equal(second.NormalizedNameEn, first.NormalizedNameEn);
        Assert.NotEqual(second.NormalizedNameAr, first.NormalizedNameAr);
        Assert.Equal(firstEnglish, first.NameEn);
    }

    [Fact]
    public void Create_NormalizesAfterTrimming_AndPreservesDisplayNames()
    {
        var specialty = Specialty.Create("  طب الأطفال  ", "  Pediatrics  ").Value;

        Assert.Equal("طب الأطفال", specialty.NameAr);
        Assert.Equal("Pediatrics", specialty.NameEn);
        Assert.Equal("طب الاطفال", specialty.NormalizedNameAr);
        Assert.Equal("PEDIATRICS", specialty.NormalizedNameEn);
    }

    [Theory]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("هدى", "هدي")]
    [InlineData("رؤى", "روى")]
    [InlineData("أطفال", "طب الأطفال")]
    public void Create_DoesNotMergeOtherArabicLettersOrDifferentPhrases(string firstName, string secondName)
    {
        var first = Specialty.Create(firstName, "First").Value;
        var second = Specialty.Create(secondName, "Second").Value;

        Assert.NotEqual(first.NormalizedNameAr, second.NormalizedNameAr);
    }

    [Fact]
    public void Create_UsesInvariantEnglishCasing()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

            var specialty = Specialty.Create("أطفال", "pediatrics").Value;

            Assert.Equal("PEDIATRICS", specialty.NormalizedNameEn);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
