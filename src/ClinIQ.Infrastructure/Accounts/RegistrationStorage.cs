using System.Security.Cryptography;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Clinics;
using ClinIQ.Application.Features.Clinics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class RegistrationStorage
{
    public string Root { get; }

    public RegistrationStorage(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Auth:PrivateStorageRoot"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Configure Auth:PrivateStorageRoot outside the deployed application.");
            configured = "../../.artifacts/auth-private";
        }
        Root = Path.GetFullPath(configured, environment.ContentRootPath);
        var appRoot = Path.GetFullPath(environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Root.Equals(appRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            Root.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private storage must be outside the application directory.");
    }

    public async Task<Result<PreparedEvidence>> ReadAsync(Guid registrationId, RegistrationUpload upload, CancellationToken cancellationToken)
    {
        var read = await ReadFileAsync(registrationId, upload.FileName, upload.Length, upload.Content, AccountErrors.InvalidDocument, cancellationToken);
        if (read.IsError) return Result<PreparedEvidence>.Failure(read.Errors);
        var file = read.Value;
        return new PreparedEvidence(new DoctorEvidenceMetadata(upload.Kind, file.StorageKey, file.FileName, file.MediaType, file.Content.LongLength, file.Sha256), file.Content);
    }

    public async Task<Result<PreparedLocationEvidence>> ReadAsync(Guid submissionId, PracticeLocationUpload upload, CancellationToken cancellationToken)
    {
        var read = await ReadFileAsync(submissionId, upload.FileName, upload.Length, upload.Content,
            description => Error.Validation("PracticeLocation.InvalidDocument", description, "Documents"), cancellationToken);
        if (read.IsError) return Result<PreparedLocationEvidence>.Failure(read.Errors);
        var file = read.Value;
        return new PreparedLocationEvidence(new PracticeLocationEvidenceMetadata(upload.Kind, file.StorageKey, file.FileName,
            file.MediaType, file.Content.LongLength, file.Sha256), file.Content);
    }

    private sealed record PreparedFile(string StorageKey, string FileName, string MediaType, string Sha256, byte[] Content);
    public async Task<Result<PreparedClinicEvidence>> ReadAsync(Guid submissionId, ClinicEvidenceUpload upload, CancellationToken ct)
    {
        var read = await ReadFileAsync(submissionId, upload.FileName, upload.Length, upload.Content,
            description => Error.Validation("ClinicApplication.InvalidDocument", description, "Documents"), ct);
        if (read.IsError) return Result<PreparedClinicEvidence>.Failure(read.Errors);
        var file = read.Value;
        return new PreparedClinicEvidence(new ClinicEvidenceMetadata(upload.Kind, file.StorageKey, file.FileName, file.MediaType, file.Content.LongLength, file.Sha256), file.Content);
    }
    private static async Task<Result<PreparedFile>> ReadFileAsync(Guid ownerId, string fileName, long length, Stream content,
        Func<string, Error> invalid, CancellationToken cancellationToken)
    {
        if (length is <= 0 or > DoctorEvidenceMetadata.MaxLength)
            return invalid("Each document must contain 1 byte to 5 MiB.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = extension switch { ".pdf" => "application/pdf", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => null };
        if (mediaType is null)
            return invalid("Only PDF, JPEG and PNG documents are accepted.");
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > DoctorEvidenceMetadata.MaxLength)
                return invalid("A document exceeds 5 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length != length || !HasSignature(bytes, mediaType))
            return invalid("Document content does not match its declared length or file type.");
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (name.Length is < 1 or > 255 || name.Any(char.IsControl))
            return invalid("Document file name is invalid.");
        return new PreparedFile($"{ownerId:N}/{Guid.NewGuid():N}{extension}", name, mediaType, Convert.ToHexString(SHA256.HashData(bytes)), bytes);
    }

    public Task WriteAsync(PreparedEvidence document, CancellationToken cancellationToken) =>
        WriteFileAsync(document.Metadata.StorageKey, document.Content, cancellationToken);

    public Task WriteAsync(PreparedLocationEvidence document, CancellationToken cancellationToken) =>
        WriteFileAsync(document.Metadata.StorageKey, document.Content, cancellationToken);

    private async Task WriteFileAsync(string storageKey, byte[] content, CancellationToken cancellationToken)
    {
        var path = GetEvidencePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = false;
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            created = true;
            await output.WriteAsync(content, cancellationToken);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }
    public Task WriteAsync(PreparedClinicEvidence document, CancellationToken ct) => WriteFileAsync(document.Metadata.StorageKey, document.Content, ct);

    public void DeleteCreatedEvidence(string storageKey) => File.Delete(GetEvidencePath(storageKey));

    public Task<Result<Stream>> OpenReadAsync(DoctorEvidenceDocument document, CancellationToken cancellationToken) =>
        OpenReadAsync(document.StorageKey, document.Length, document.Sha256, cancellationToken);

    public Task<Result<Stream>> OpenReadAsync(ProfessionalChangeDocument document, CancellationToken cancellationToken) =>
        OpenReadAsync(document.StorageKey, document.Length, document.Sha256, cancellationToken);

    public Task<Result<Stream>> OpenReadAsync(PracticeLocationDocument document, CancellationToken cancellationToken) =>
        OpenReadAsync(document.StorageKey, document.Length, document.Sha256, cancellationToken);
    public Task<Result<Stream>> OpenReadAsync(ClinIQ.Domain.PremisesCorrections.PremisesCorrectionDocument document, CancellationToken cancellationToken) =>
        OpenReadAsync(document.StorageKey, document.Length, document.Sha256, cancellationToken);
    public Task<Result<Stream>> OpenReadAsync(ClinicRegistrationDocument document, CancellationToken ct) =>
        OpenReadAsync(document.StorageKey, document.Length, document.Sha256, ct);

    private async Task<Result<Stream>> OpenReadAsync(string key, long length, string sha256, CancellationToken cancellationToken)
    {
        FileStream stream;
        try { stream = new FileStream(GetEvidencePath(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return DoctorReviewErrors.EvidenceUnavailable; }
        try
        {
            if (length is <= 0 or > DoctorEvidenceMetadata.MaxLength || stream.Length != length)
            {
                await stream.DisposeAsync();
                return DoctorReviewErrors.EvidenceUnavailable;
            }
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!string.Equals(Convert.ToHexString(hash), sha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync();
                return DoctorReviewErrors.EvidenceUnavailable;
            }
            stream.Position = 0;
            return Result<Stream>.Success(stream);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private string GetEvidencePath(string storageKey)
    {
        var evidenceRoot = Path.Combine(Root, "doctor-evidence") + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(evidenceRoot, storageKey));
        if (!path.StartsWith(evidenceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid private document storage key.");
        return path;
    }

    private static bool HasSignature(byte[] bytes, string mediaType) => mediaType switch
    {
        "application/pdf" => bytes.AsSpan().StartsWith("%PDF-"u8),
        "image/jpeg" => bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff &&
            bytes[^2] == 0xff && bytes[^1] == 0xd9,
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        _ => false
    };
}

public sealed record PreparedEvidence(DoctorEvidenceMetadata Metadata, byte[] Content);
public sealed record PreparedLocationEvidence(PracticeLocationEvidenceMetadata Metadata, byte[] Content);
public sealed record PreparedClinicEvidence(ClinicEvidenceMetadata Metadata, byte[] Content);
