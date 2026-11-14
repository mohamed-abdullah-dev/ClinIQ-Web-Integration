using System.Globalization;
using ClinIQ.Domain.Specialties;
using Xunit;

namespace ClinIQ.Domain.Tests.Specialties;

public sealed class SpecialtyNameComparisonTests
{
    // Arabic pairs: expected equality of the domain-generated comparison keys.
    [Theory]
    [InlineData("أطفال", "اطفال", true)]
    [InlineData("إطفال", "اطفال", true)]
    [InlineData("آطفال", "اطفال", true)]
    [InlineData("طب الأطفال", "طب الاطفال", true)]
    [InlineData("أنف وأذن", "انف واذن", true)]
    [InlineData("أمراض الأطفال", "امراض الاطفال", true)]
    [InlineData("جراحة الأورام", "جراحة الاورام", true)]
    [InlineData("أمراض الأعصاب", "امراض الاعصاب", true)]
    [InlineData("  أطفال  ", "اطفال", true)]
    [InlineData("\tأطفال\r\n", "اطفال", true)]
    [InlineData("\u00A0أطفال\u00A0", "اطفال", true)]
    [InlineData("ا\u0654طفال", "أطفال", true)]
    [InlineData("ا\u0655طفال", "إطفال", true)]
    [InlineData("ا\u0653طفال", "آطفال", true)]
    [InlineData("Pediatrics أطفال", "pediatrics اطفال", true)]
    [InlineData("طب الأطفال", "طب  الأطفال", false)]
    [InlineData("طب الأطفال", "طب\tالأطفال", false)]
    [InlineData("طب الأطفال", "طب\u00A0الأطفال", false)]
    [InlineData("أَطفال", "أطفال", false)]
    [InlineData("طبّ الأطفال", "طب الأطفال", false)]
    [InlineData("أطفــال", "أطفال", false)]
    [InlineData("جلدية", "جلديه", false)]
    [InlineData("مرضى", "مرضي", false)]
    [InlineData("مسؤول", "مسوول", false)]
    [InlineData("نساء وتوليد", "نساء و توليد", false)]
    [InlineData("أطفال", "طب الأطفال", false)]
    [InlineData("أطفال\u200B", "أطفال", false)]
    [InlineData("ا\u064E\u0654طفال", "أَطفال", false)]
    public void ArabicPairs_ProduceExpectedKeysWithoutChangingDisplayNames(
        string firstName, string secondName, bool expectedEqual)
    {
        var first = Specialty.Create(firstName, "First").Value;
        var second = Specialty.Create(secondName, "Second").Value;

        Assert.Equal(expectedEqual,
            string.Equals(first.NormalizedNameAr, second.NormalizedNameAr, StringComparison.Ordinal));
        Assert.Equal(firstName.Trim(), first.NameAr);
        Assert.Equal(secondName.Trim(), second.NameAr);
        Assert.NotEqual(first.NormalizedNameEn, second.NormalizedNameEn);
    }

    // English pairs: only trimming and invariant casing are applied.
    [Theory]
    [InlineData("Pediatrics", "pediatrics", true)]
    [InlineData("PEDIATRICS", "Pediatrics", true)]
    [InlineData("Cardiology", "CARDIOLOGY", true)]
    [InlineData("Ear Nose and Throat", "ear nose AND throat", true)]
    [InlineData("  Pediatrics  ", "Pediatrics", true)]
    [InlineData("\tPediatrics\r\n", "pediatrics", true)]
    [InlineData("\u00A0Pediatrics\u00A0", "Pediatrics", true)]
    [InlineData("Café", "CAFÉ", true)]
    [InlineData("Pediatrics", "Paediatrics", false)]
    [InlineData("Orthopedics", "Orthopaedics", false)]
    [InlineData("ENT", "Ear Nose and Throat", false)]
    [InlineData("Ear Nose and Throat", "Ear  Nose and Throat", false)]
    [InlineData("Cardiology", "Cardio-logy", false)]
    [InlineData("Cafe", "Café", false)]
    [InlineData("Cafe\u0301", "Café", false)]
    [InlineData("Pediatrics\u200B", "Pediatrics", false)]
    public void EnglishPairs_ProduceExpectedKeysWithoutChangingDisplayNames(
        string firstName, string secondName, bool expectedEqual)
    {
        var first = Specialty.Create("الأول", firstName).Value;
        var second = Specialty.Create("الثاني", secondName).Value;

        Assert.Equal(expectedEqual,
            string.Equals(first.NormalizedNameEn, second.NormalizedNameEn, StringComparison.Ordinal));
        Assert.Equal(firstName.Trim(), first.NameEn);
        Assert.Equal(secondName.Trim(), second.NameEn);
        Assert.NotEqual(first.NormalizedNameAr, second.NormalizedNameAr);
    }

    [Theory]
    [InlineData("  طب الأطفال  ", "  Pediatrics  ")]
    [InlineData("أنف وأذن", "Ear Nose and Throat")]
    [InlineData("ا\u0654طفال", "pediatrics")]
    [InlineData("طب  الأطفال", "Ear  Nose and Throat")]
    [InlineData("طبّ الأطفال", "Café")]
    [InlineData("أطفال\u200B", "Pediatrics\u200B")]
    public void ComparisonKeys_AreStableWhenNormalizedAgain(string nameAr, string nameEn)
    {
        var first = Specialty.Create(nameAr, nameEn).Value;
        var second = Specialty.Create(first.NormalizedNameAr, first.NormalizedNameEn).Value;

        Assert.Equal(first.NormalizedNameAr, second.NormalizedNameAr);
        Assert.Equal(first.NormalizedNameEn, second.NormalizedNameEn);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-EG")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void ComparisonKeys_DoNotDependOnCurrentCulture(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            var specialty = Specialty.Create("أطفال pediatrics", "pediatrics").Value;

            Assert.Equal("اطفال PEDIATRICS", specialty.NormalizedNameAr);
            Assert.Equal("PEDIATRICS", specialty.NormalizedNameEn);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(199)]
    [InlineData(200)]
    public void ComparisonKeys_RespectLengthBoundaryWithoutTruncation(int length)
    {
        var specialty = Specialty.Create(new string('أ', length), new string('i', length)).Value;

        Assert.Equal(new string('ا', length), specialty.NormalizedNameAr);
        Assert.Equal(new string('I', length), specialty.NormalizedNameEn);
        Assert.Equal(length, specialty.NameAr.Length);
        Assert.Equal(length, specialty.NameEn.Length);
        Assert.Equal(length, specialty.NormalizedNameAr.Length);
        Assert.Equal(length, specialty.NormalizedNameEn.Length);
    }
}
