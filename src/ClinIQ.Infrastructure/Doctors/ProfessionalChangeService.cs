using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.DoctorProfessionalChanges;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Doctors;

public sealed class ProfessionalChangeService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser,
    TimeProvider clock, RegistrationStorage storage, ILogger<ProfessionalChangeService> logger) : IProfessionalChangeService
{
    private async Task<Result<Guid>> ActorAsync(bool admin, CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return ProfessionalChangeErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed || !await users.IsInRoleAsync(user, admin ? "Admin" : "Doctor"))
            return ProfessionalChangeErrors.Forbidden;
        if (!admin && !await db.DoctorProfiles.AnyAsync(profile => profile.UserId == id &&
            db.DoctorRegistrations.Any(registration => registration.Id == profile.DoctorRegistrationId &&
                registration.UserId == id && registration.IsCurrent && registration.Status == DoctorRegistrationStatus.Approved), ct))
            return ProfessionalChangeErrors.Forbidden;
        return id;
    }

    public async Task<Result<VerifiedDoctorProfileDto>> GetOwnProfileAsync(CancellationToken ct)
    {
        var actor = await ActorAsync(false, ct);
        if (actor.IsError) return Result<VerifiedDoctorProfileDto>.Failure(actor.Errors);
        var profile = await db.DoctorProfiles.AsNoTracking().SingleAsync(x => x.UserId == actor.Value, ct);
        return Profile(profile);
    }

    public async Task<Result<ProfessionalChangePage>> ListAsync(bool admin, int page, int pageSize, ProfessionalChangeStatus? status, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct);
        if (actor.IsError) return Result<ProfessionalChangePage>.Failure(actor.Errors);
        var query = db.DoctorProfessionalChanges.AsNoTracking();
        if (!admin) query = query.Where(x => x.UserId == actor.Value);
        if (status is not null) query = query.Where(x => x.Status == status);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new ProfessionalChangePage(rows.Select(Summary).ToArray(), count, page, pageSize);
    }

    public async Task<Result<ProfessionalChangeDetail>> GetAsync(Guid id, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct);
        if (actor.IsError) return Result<ProfessionalChangeDetail>.Failure(actor.Errors);
        var request = await db.DoctorProfessionalChanges.AsNoTracking().Include(x => x.Documents)
            .SingleOrDefaultAsync(x => x.Id == id && (admin || x.UserId == actor.Value), ct);
        if (request is null) return ProfessionalChangeErrors.NotFound;
        var profile = await db.DoctorProfiles.AsNoTracking().SingleAsync(x => x.Id == request.DoctorProfileId, ct);
        return new ProfessionalChangeDetail(Summary(request), Profile(profile), request.OriginalProfessionalName,
            request.OriginalSpecialtyId, request.OriginalProfessionalGrade, Convert.ToBase64String(request.OriginalProfileVersion),
            request.Documents.OrderBy(x => x.Kind).Select(x => new ChangeEvidenceDto(x.Id, x.Kind, x.OriginalFileName, x.MediaType, x.Length)).ToArray(),
            request.ReviewerUserId, request.ReviewReason, request.ReviewedAtUtc);
    }

    public async Task<Result<ProfessionalChangeSummary>> SubmitAsync(string name, Guid specialtyId, ProfessionalGrade grade, string profileVersion,
        IReadOnlyList<RegistrationUpload> documents, CancellationToken ct)
    {
        EnsureFreshContext();
        var actor = await ActorAsync(false, ct);
        if (actor.IsError) return Result<ProfessionalChangeSummary>.Failure(actor.Errors);
        var snapshot = await db.DoctorProfiles.AsNoTracking().SingleAsync(x => x.UserId == actor.Value, ct);
        if (!Matches(snapshot.Version, profileVersion)) return ProfessionalChangeErrors.StaleProfile;
        if (!await db.Specialties.AnyAsync(x => x.Id == specialtyId, ct)) return ProfessionalChangeErrors.UnknownSpecialty;
        if (await db.DoctorProfessionalChanges.AnyAsync(x => x.DoctorProfileId == snapshot.Id && x.Status == ProfessionalChangeStatus.PendingReview, ct))
            return ProfessionalChangeErrors.PendingExists;
        var id = Guid.NewGuid(); var prepared = new List<PreparedEvidence>();
        foreach (var upload in documents)
        {
            var read = await storage.ReadAsync(id, upload, ct);
            if (read.IsError) return Result<ProfessionalChangeSummary>.Failure(read.Errors);
            prepared.Add(read.Value);
        }
        var request = DoctorProfessionalChange.Create(id, snapshot, name, specialtyId, grade, prepared.Select(x => x.Metadata).ToArray());
        if (request.IsError) return Result<ProfessionalChangeSummary>.Failure(request.Errors);
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync(snapshot.Id, ct)) return ProfessionalChangeErrors.Busy;
            var current = await db.DoctorProfiles.AsNoTracking().SingleAsync(x => x.Id == snapshot.Id, ct);
            if (!Matches(current.Version, profileVersion)) return ProfessionalChangeErrors.StaleProfile;
            if (await db.DoctorProfessionalChanges.AnyAsync(x => x.DoctorProfileId == snapshot.Id && x.Status == ProfessionalChangeStatus.PendingReview, ct))
                return ProfessionalChangeErrors.PendingExists;
            foreach (var document in prepared)
            {
                await storage.WriteAsync(document, ct); written.Add(document.Metadata.StorageKey);
            }
            db.DoctorProfessionalChanges.Add(request.Value);
            await db.SaveChangesAsync(ct);
            commitAttempted = true; await transaction.CommitAsync(ct); committed = true;
            return Summary(request.Value);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql && sql.Number is 2601 or 2627 &&
            sql.Message.Contains("IX_DoctorProfessionalChanges_DoctorProfileId", StringComparison.Ordinal))
        { return ProfessionalChangeErrors.PendingExists; }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 547 } sql &&
            sql.Message.Contains("FK_DoctorProfessionalChanges_Specialties_", StringComparison.Ordinal))
        { return ProfessionalChangeErrors.UnknownSpecialty; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { logger.LogError("Uncommitted professional evidence cleanup failed with {ExceptionType}.", error.GetType().Name); throw; }
                }
            else if (!committed) logger.LogError("Professional-change commit outcome is uncertain; private evidence retained for reconciliation.");
        }
    }

    public async Task<Result<ProfessionalChangeSummary>> ReviewAsync(Guid id, bool approve, string reason, string version, CancellationToken ct)
    {
        EnsureFreshContext();
        var actor = await ActorAsync(true, ct);
        if (actor.IsError) return Result<ProfessionalChangeSummary>.Failure(actor.Errors);
        var parent = await db.DoctorProfessionalChanges.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.DoctorProfileId).SingleOrDefaultAsync(ct);
        if (parent is null) return ProfessionalChangeErrors.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(parent.Value, ct)) return ProfessionalChangeErrors.Busy;
        var request = await db.DoctorProfessionalChanges.SingleAsync(x => x.Id == id, ct);
        if (request.Status != ProfessionalChangeStatus.PendingReview) return ProfessionalChangeErrors.AlreadyReviewed;
        if (!Matches(request.Version, version)) return ProfessionalChangeErrors.StaleRequest;
        var profile = await db.DoctorProfiles.SingleAsync(x => x.Id == parent.Value, ct);
        var reviewed = request.Review(profile, approve, reason, actor.Value, clock.GetUtcNow());
        if (reviewed.IsError) return Result<ProfessionalChangeSummary>.Failure(reviewed.Errors);
        try
        {
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Summary(request);
        }
        catch (DbUpdateConcurrencyException) { return ProfessionalChangeErrors.StaleProfile; }
    }

    public async Task<Result<ChangeEvidenceDownload>> DownloadAsync(Guid id, Guid documentId, bool admin, CancellationToken ct)
    {
        EnsureFreshContext();
        var actor = await ActorAsync(admin, ct);
        if (actor.IsError) return Result<ChangeEvidenceDownload>.Failure(actor.Errors);
        var document = await db.ProfessionalChangeDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.RequestId == id &&
            db.DoctorProfessionalChanges.Any(request => request.Id == x.RequestId && (admin || request.UserId == actor.Value)), ct);
        if (document is null) return ProfessionalChangeErrors.NotFound;
        var opened = await storage.OpenReadAsync(document, ct);
        if (opened.IsError) return ProfessionalChangeErrors.EvidenceUnavailable;
        try
        {
            db.ProfessionalChangeEvidenceAccesses.Add(ProfessionalChangeEvidenceAccess.Grant(document.Id, actor.Value, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct); return new ChangeEvidenceDownload(opened.Value, document.MediaType, document.OriginalFileName);
        }
        catch { await opened.Value.DisposeAsync(); throw; }
    }

    private void EnsureFreshContext()
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Professional changes require a context without pending writes.");
    }
    private async Task<bool> LockAsync(Guid profileId, CancellationToken ct)
    {
        var resource = $"ClinIQ.DoctorProfessionalProfile.{profileId:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected professional-profile lock failure.");
        ct.ThrowIfCancellationRequested(); return codes.Single() >= 0;
    }
    private static bool Matches(byte[] version, string candidate) => string.Equals(Convert.ToBase64String(version), candidate, StringComparison.Ordinal);
    private static VerifiedDoctorProfileDto Profile(DoctorProfile profile) =>
        new(profile.Id, profile.UserId, profile.ProfessionalName, profile.SpecialtyId, profile.ProfessionalGrade, Convert.ToBase64String(profile.Version));
    private static ProfessionalChangeSummary Summary(DoctorProfessionalChange request) => new(request.Id, request.DoctorProfileId, request.UserId,
        request.ProfessionalName, request.SpecialtyId, request.ProfessionalGrade, request.Status.ToString(), request.CreatedAtUtc, Convert.ToBase64String(request.Version));
}
