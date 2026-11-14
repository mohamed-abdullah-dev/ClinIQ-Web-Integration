using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Doctors;

public sealed class DoctorRegistrationTests
{
    [Fact]
    public void Create_AlwaysStartsPending_AndPreservesTheSubmittedRelationship()
    {
        var userId = Guid.NewGuid();
        var specialtyId = Guid.NewGuid();
        var result = Create(userId: userId, specialtyId: specialtyId);
        Assert.True(result.IsSuccess);
        Assert.Equal(DoctorRegistrationStatus.PendingReview, result.Value.Status);
        Assert.Equal(userId, result.Value.UserId);
        Assert.Equal(specialtyId, result.Value.SpecialtyId);
        Assert.Equal("Doctor Name", result.Value.ProfessionalName);
        Assert.Equal(4, result.Value.Documents.Count);
        Assert.All(result.Value.Documents, document => Assert.Equal(result.Value.Id, document.DoctorRegistrationId));
    }

    [Theory]
    [InlineData(DoctorEvidenceKind.PracticeLicense)]
    [InlineData(DoctorEvidenceKind.SyndicateRegistration)]
    [InlineData(DoctorEvidenceKind.Identity)]
    [InlineData(DoctorEvidenceKind.ProfessionalGrade)]
    public void MissingRequiredEvidence_Fails(DoctorEvidenceKind missing)
    {
        var result = Create(documents: Evidence().Where(document => document.Kind != missing).ToArray());
        Assert.True(result.IsError);
        Assert.Contains(result.Errors, error => error.Code == "DoctorRegistration.Documents");
    }

    [Fact]
    public void RepeatedEvidenceKind_DoesNotReplaceMissingEvidence()
    {
        var documents = Evidence().ToArray();
        documents[3] = documents[0] with { StorageKey = "other.pdf" };
        Assert.True(Create(documents: documents).IsError);
    }

    [Fact]
    public void GeneralPractitioner_DoesNotRequireASpecialistGradeDocument()
    {
        var result = Create(grade: ProfessionalGrade.GeneralPractitioner,
            documents: Evidence().Where(document => document.Kind != DoctorEvidenceKind.ProfessionalGrade).ToArray());
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Documents.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingProfessionalName_IsRejected(string name) => Assert.True(Create(name: name).IsError);

    [Fact]
    public void UnknownGrade_AndMissingSpecialty_AreRejected()
    {
        var result = Create(grade: (ProfessionalGrade)99, specialtyId: Guid.Empty);
        Assert.True(result.IsError);
        Assert.Contains(result.Errors, error => error.Field == "ProfessionalGrade");
        Assert.Contains(result.Errors, error => error.Field == "SpecialtyId");
    }

    [Fact]
    public void InvalidMetadata_IsRejected()
    {
        var documents = Evidence().ToArray();
        documents[0] = documents[0] with { Length = DoctorEvidenceMetadata.MaxLength + 1 };
        Assert.True(Create(documents: documents).IsError);
    }

    private static ClinIQ.Domain.Common.Results.Result<DoctorRegistration> Create(
        string name = "  Doctor Name  ", ProfessionalGrade grade = ProfessionalGrade.Specialist,
        Guid? userId = null, Guid? specialtyId = null, IReadOnlyList<DoctorEvidenceMetadata>? documents = null) =>
        DoctorRegistration.Create(Guid.NewGuid(), userId ?? Guid.NewGuid(), name, "01012345678", "12345",
            specialtyId ?? Guid.NewGuid(), grade, documents ?? Evidence());

    private static DoctorEvidenceMetadata[] Evidence() => Enum.GetValues<DoctorEvidenceKind>()
        .Select(kind => new DoctorEvidenceMetadata(kind, $"{Guid.NewGuid():N}.pdf", "evidence.pdf", "application/pdf", 10, new string('A', 64)))
        .ToArray();
}
