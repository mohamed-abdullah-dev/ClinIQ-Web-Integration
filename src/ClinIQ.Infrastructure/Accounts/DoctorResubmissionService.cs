using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorResubmissions;
using ClinIQ.Application.Features.DoctorResubmissions.Commands;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class DoctorResubmissionOptions
{
    public string? Url { get; set; }
}

public sealed class DoctorResubmissionService(AppDbContext db, UserManager<AppUser> users, RegistrationStorage storage,
    TimeProvider clock, IHostEnvironment environment, IOptions<DoctorResubmissionOptions> options,
    ILogger<DoctorResubmissionService> logger) : IDoctorResubmissionService
{
    private readonly string? _url = options.Value.Url;

    public async Task RequestLinkAsync(string email, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        try
        {
            EnsureFreshContext();
            // A real outbound provider is deliberately still unavailable outside Development.
            if (!environment.IsDevelopment() || _url is null) return;
            var normalized = users.NormalizeEmail(email.Trim());
            var userId = await db.Users.Where(x => x.NormalizedEmail == normalized).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
            if (userId is null) return;
            string token;
            string recipient;
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                if (!await LockAsync(userId.Value, ct)) return;
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.NormalizedEmail == normalized, ct);
                if (user?.SecurityStamp is null || user.NormalizedEmail is null || user.Email is null) return;
                var row = await db.DoctorRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id && x.IsCurrent, ct);
                if (row?.Status != DoctorRegistrationStatus.Rejected) return;
                var now = clock.GetUtcNow();
                var latest = await db.Set<DoctorResubmissionGrant>().Where(x => x.RegistrationId == row.Id)
                    .OrderByDescending(x => x.IssuedAtUtc).FirstOrDefaultAsync(ct);
                if (latest is not null && now < latest.IssuedAtUtc.AddMinutes(1)) return;
                // Save retirement first so a new active grant cannot precede it in EF's insert ordering.
                var active = await db.Set<DoctorResubmissionGrant>().SingleOrDefaultAsync(x => x.RegistrationId == row.Id && x.ConsumedAtUtc == null, ct);
                if (active is not null) { active.Consume(now); await db.SaveChangesAsync(ct); }
                var secret = SessionSecrets.Create();
                var grant = new DoctorResubmissionGrant(row.Id, user.Id, SessionSecrets.Hash(secret), user.SecurityStamp,
                    user.NormalizedEmail, row.Version, now);
                db.Add(grant); await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                token = SessionSecrets.Format(grant.Id, secret); recipient = user.Email;
            }
            await DeliverAsync(recipient, token, ct);
        }
        finally
        {
            var remaining = TimeSpan.FromMilliseconds(500) - clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero && !ct.IsCancellationRequested) await Task.Delay(remaining, clock, ct);
        }
    }

    public async Task<Result<DoctorResubmissionPreview>> GetAsync(string token, CancellationToken ct)
    {
        var proof = await ValidateAsync(token, false, ct);
        if (proof is null) return DoctorResubmissionErrors.InvalidLink;
        var row = proof.Registration;
        var reason = await db.DoctorRegistrationReviews.AsNoTracking().Where(x => x.DoctorRegistrationId == row.Id)
            .Select(x => x.Reason).SingleAsync(ct);
        var documents = await db.Set<DoctorEvidenceDocument>().AsNoTracking().Where(x => x.DoctorRegistrationId == row.Id)
            .OrderBy(x => x.Kind).Select(x => new DoctorEvidenceDto(x.Id, x.Kind, x.OriginalFileName, x.MediaType, x.Length)).ToArrayAsync(ct);
        return new DoctorResubmissionPreview(row.Id, row.ProfessionalName, row.PhoneNumber, row.RegistrationNumber,
            row.SpecialtyId, row.ProfessionalGrade, row.AttemptNumber, reason, documents);
    }

    public async Task<Result<DoctorEvidenceDownload>> DownloadAsync(string token, Guid documentId, CancellationToken ct)
    {
        var proof = await ValidateAsync(token, false, ct);
        if (proof is null) return DoctorResubmissionErrors.InvalidLink;
        var document = await db.Set<DoctorEvidenceDocument>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == documentId && x.DoctorRegistrationId == proof.Registration.Id, ct);
        if (document is null) return DoctorReviewErrors.NotFound;
        var opened = await storage.OpenReadAsync(document, ct);
        if (opened.IsError) return Result<DoctorEvidenceDownload>.Failure(opened.Errors);
        try
        {
            db.DoctorEvidenceAccesses.Add(DoctorEvidenceAccess.Grant(document.Id, proof.Registration.UserId, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return new DoctorEvidenceDownload(opened.Value, document.MediaType, document.OriginalFileName);
        }
        catch { await opened.Value.DisposeAsync(); throw; }
    }

    public async Task<Result<DoctorResubmissionReceipt>> SubmitAsync(ResubmitDoctorCommand command, CancellationToken ct)
    {
        EnsureFreshContext();
        var initial = await ValidateAsync(command.Token, false, ct);
        if (initial is null) return DoctorResubmissionErrors.InvalidLink;
        if (!await db.Specialties.AnyAsync(x => x.Id == command.SpecialtyId, ct)) return AccountErrors.UnknownSpecialty;
        var nextId = Guid.NewGuid(); var prepared = new List<PreparedEvidence>();
        foreach (var upload in command.Documents)
        {
            var read = await storage.ReadAsync(nextId, upload, ct);
            if (read.IsError) return Result<DoctorResubmissionReceipt>.Failure(read.Errors);
            prepared.Add(read.Value);
        }
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync(initial.Registration.UserId, ct)) return DoctorResubmissionErrors.Busy;
            var proof = await ValidateAsync(command.Token, true, ct);
            if (proof is null) return DoctorResubmissionErrors.InvalidLink;
            var next = proof.Registration.Resubmit(nextId, command.ProfessionalName, command.PhoneNumber,
                command.RegistrationNumber, command.SpecialtyId, command.ProfessionalGrade, prepared.Select(x => x.Metadata).ToArray());
            if (next.IsError) return Result<DoctorResubmissionReceipt>.Failure(next.Errors);
            proof.Grant.Consume(clock.GetUtcNow());
            // Retire the current attempt before inserting its successor; both saves commit atomically.
            await db.SaveChangesAsync(ct);
            foreach (var document in prepared)
            {
                await storage.WriteAsync(document, ct); written.Add(document.Metadata.StorageKey);
            }
            db.DoctorRegistrations.Add(next.Value); await db.SaveChangesAsync(ct);
            commitAttempted = true; await transaction.CommitAsync(ct); committed = true;
            return new DoctorResubmissionReceipt(nextId, proof.Registration.Id, next.Value.AttemptNumber, next.Value.Status.ToString());
        }
        catch (DbUpdateConcurrencyException) { return DoctorResubmissionErrors.InvalidLink; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError("Uncommitted resubmission evidence cleanup failed with {ExceptionType}.", error.GetType().Name);
                        throw;
                    }
                }
            else if (!committed)
                logger.LogError("Resubmission commit outcome is uncertain; private evidence retained for reconciliation.");
        }
    }

    private sealed record Proof(DoctorResubmissionGrant Grant, DoctorRegistration Registration);
    private async Task<Proof?> ValidateAsync(string token, bool track, CancellationToken ct)
    {
        if (!SessionSecrets.TryParse(token, out var id, out var secret)) return null;
        var grants = db.Set<DoctorResubmissionGrant>().AsQueryable();
        var grant = await (track ? grants : grants.AsNoTracking()).SingleOrDefaultAsync(x => x.Id == id, ct);
        var now = clock.GetUtcNow();
        if (grant is null || grant.ConsumedAtUtc is not null || now < grant.IssuedAtUtc || now >= grant.ExpiresAtUtc ||
            !SessionSecrets.Matches(secret, grant.SecretHash)) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grant.UserId, ct);
        if (user is null || user.SecurityStamp != grant.SecurityStamp || user.NormalizedEmail != grant.NormalizedEmail) return null;
        var rows = db.DoctorRegistrations.AsQueryable();
        var row = await (track ? rows : rows.AsNoTracking()).SingleOrDefaultAsync(x => x.Id == grant.RegistrationId && x.UserId == grant.UserId, ct);
        return row is not null && row.IsCurrent && row.Status == DoctorRegistrationStatus.Rejected &&
            row.Version.AsSpan().SequenceEqual(grant.RegistrationVersion) ? new Proof(grant, row) : null;
    }

    private void EnsureFreshContext()
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Resubmission requires a context without pending changes.");
    }
    private async Task<bool> LockAsync(Guid userId, CancellationToken ct)
    {
        var resource = $"ClinIQ.AccountCredentials.{userId:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected account lock failure.");
        return codes.Single() >= 0;
    }
    private async Task DeliverAsync(string recipient, string token, CancellationToken ct)
    {
        var path = Path.Combine(storage.Root, "doctor-resubmission-emails", $"{Guid.NewGuid():N}.json");
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            created = true;
            // Fragment keeps the bearer secret out of web-server request URLs and referrers.
            await JsonSerializer.SerializeAsync(output, new { To = recipient, Subject = "Correct your ClinIQ doctor registration",
                ResubmissionUrl = $"{_url}#token={token}" }, cancellationToken: ct);
        }
        catch (Exception error)
        {
            logger.LogWarning("Resubmission link delivery failed with {ExceptionType}.", error.GetType().Name);
            if (created)
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Partial resubmission email cleanup failed with {ExceptionType}.", cleanup.GetType().Name); }
        }
    }
}
