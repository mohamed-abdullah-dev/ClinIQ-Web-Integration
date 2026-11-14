using System.Security.Cryptography;
using System.Text;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class PracticeLocationStorageTests
{
    private static readonly byte[] Evidence = Encoding.UTF8.GetBytes("%PDF-1.7\nSynthetic facility evidence.\n%%EOF");

    [Theory]
    [InlineData(PracticeLocationEvidenceKind.FacilityLicense)]
    [InlineData(PracticeLocationEvidenceKind.WorkAuthorization)]
    public async Task LocationEvidenceRetainsItsKindAndGetsAPrivateRandomStorageKey(PracticeLocationEvidenceKind kind)
    {
        var storage = Storage(); var submission = Guid.NewGuid(); await using var content = new MemoryStream(Evidence);
        var result = await storage.ReadAsync(submission, new PracticeLocationUpload(kind, "../../facility.pdf", "application/pdf", Evidence.Length, content), default);
        Assert.True(result.IsSuccess); var file = result.Value;
        Assert.Equal(kind, file.Metadata.Kind); Assert.True(file.Metadata.IsValid); Assert.Equal("facility.pdf", file.Metadata.OriginalFileName);
        Assert.StartsWith($"{submission:N}/", file.Metadata.StorageKey); Assert.Equal(Convert.ToHexString(SHA256.HashData(Evidence)), file.Metadata.Sha256);
        await storage.WriteAsync(file, default);
        var path = Path.Combine(storage.Root, "doctor-evidence", file.Metadata.StorageKey);
        Assert.Equal(Evidence, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<IOException>(() => storage.WriteAsync(file, default));
        Assert.Equal(Evidence, await File.ReadAllBytesAsync(path)); // A failed CreateNew never removes an existing file.
    }

    [Theory]
    [InlineData("facility.exe", false, 0)]
    [InlineData("facility.pdf", true, 0)]
    [InlineData("facility.pdf", false, -1)]
    [InlineData("facility.pdf", false, 1)]
    [InlineData("bad\nname.pdf", false, 0)]
    public async Task InvalidUploadsFailBeforeAnyFileIsWritten(string name, bool wrongMagic, int lengthDifference)
    {
        var storage = Storage(); var bytes = wrongMagic ? Encoding.UTF8.GetBytes("Not a PDF") : Evidence;
        await using var content = new MemoryStream(bytes);
        var result = await storage.ReadAsync(Guid.NewGuid(), new PracticeLocationUpload(PracticeLocationEvidenceKind.FacilityLicense,
            name, "application/pdf", bytes.Length + lengthDifference, content), default);
        Assert.True(result.IsError); Assert.All(result.Errors, x => Assert.Equal("PracticeLocation.InvalidDocument", x.Code));
        Assert.False(Directory.Exists(storage.Root));
    }

    [Fact]
    public async Task DoctorUploadContractStillUsesDoctorKindsAndErrorCodes()
    {
        var storage = Storage(); await using var content = new MemoryStream(Evidence);
        var valid = await storage.ReadAsync(Guid.NewGuid(), new RegistrationUpload(DoctorEvidenceKind.Identity, "doctor.pdf", "application/pdf", Evidence.Length, content), default);
        Assert.True(valid.IsSuccess); Assert.Equal(DoctorEvidenceKind.Identity, valid.Value.Metadata.Kind);
        await using var invalidContent = new MemoryStream(Evidence);
        var invalid = await storage.ReadAsync(Guid.NewGuid(), new RegistrationUpload(DoctorEvidenceKind.Identity, "doctor.pdf", "application/pdf", 0, invalidContent), default);
        Assert.Equal("DoctorRegistration.InvalidDocument", Assert.Single(invalid.Errors).Code);
    }

    [Fact]
    public async Task CancellationCleansOnlyTheNewPartialLocationFile()
    {
        var storage = Storage(); await using var input = new MemoryStream(Evidence);
        var file = (await storage.ReadAsync(Guid.NewGuid(), new PracticeLocationUpload(PracticeLocationEvidenceKind.FacilityLicense,
            "facility.pdf", "application/pdf", Evidence.Length, input), default)).Value;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync(file, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(storage.Root, "doctor-evidence", file.Metadata.StorageKey)));
    }

    [Fact]
    public async Task CraftedStorageKeysCannotWriteOutsideThePrivateEvidenceDirectory()
    {
        var storage = Storage(); var metadata = new PracticeLocationEvidenceMetadata(PracticeLocationEvidenceKind.FacilityLicense,
            "../escape.pdf", "facility.pdf", "application/pdf", Evidence.Length, Convert.ToHexString(SHA256.HashData(Evidence)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteAsync(new PreparedLocationEvidence(metadata, Evidence), default));
        Assert.False(Directory.Exists(storage.Root));
    }

    private static RegistrationStorage Storage()
    {
        var root = Integration.DoctorReviewFixture.RepositoryRoot;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Auth:PrivateStorageRoot"] = Path.Combine(root, ".artifacts", "independent-practice-locations", "file-tests", Guid.NewGuid().ToString("N")) }).Build();
        return new RegistrationStorage(new EnvironmentStub { ContentRootPath = Path.Combine(root, "src", "ClinIQ.API") }, configuration);
    }
    private sealed class EnvironmentStub : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
