using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.PremisesCorrections;

public sealed class PremisesCorrectionService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser,
    TimeProvider clock, RegistrationStorage storage, ILogger<PremisesCorrectionService> logger) : IPremisesCorrectionService
{
    private async Task<Result<Guid>> ActorAsync(bool admin, CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return PremisesCorrectionErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed) return PremisesCorrectionErrors.Forbidden;
        var roles = await users.GetRolesAsync(user);
        if (admin ? !roles.Contains("Admin") : !roles.Any(x => x is "Doctor" or "ClinicManager" or "ClinicApplicant"))
            return PremisesCorrectionErrors.Forbidden;
        return id;
    }
    private Task<bool> EligibleDoctorAsync(Guid id, CancellationToken ct) => db.DoctorProfiles.AnyAsync(p => p.UserId == id &&
        db.Users.Any(u => u.Id == id && u.EmailConfirmed) &&
        db.UserRoles.Any(ur => ur.UserId == id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "DOCTOR")) &&
        db.DoctorRegistrations.Any(r => r.Id == p.DoctorRegistrationId && r.UserId == id && r.IsCurrent && r.Status == DoctorRegistrationStatus.Approved), ct);
    private Task<bool> ManagesAsync(Guid userId, Guid clinicId, CancellationToken ct) => db.ClinicManagementMemberships.AnyAsync(m =>
        m.UserId == userId && m.ClinicId == clinicId && m.IsActive && db.Clinics.Any(c => c.Id == clinicId && c.IsActive) &&
        db.Users.Any(u => u.Id == userId && u.EmailConfirmed) &&
        db.UserRoles.Any(ur => ur.UserId == userId && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "CLINICMANAGER")), ct);

    public async Task<Result<PremisesCorrectionDto>> SubmitAsync(Guid targetId, bool branch, PremisesDetails proposed, string reason,
        string targetVersion, IReadOnlyList<PracticeLocationUpload> documents, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct);
        if (actor.IsError) return Result<PremisesCorrectionDto>.Failure(actor.Errors);
        var normalized = proposed.Normalize(branch);
        if (normalized.IsError) return Result<PremisesCorrectionDto>.Failure(normalized.Errors);
        // Authorize before reading uploads or exposing target versions.
        var target = await TargetAsync(targetId, branch, actor.Value, ct);
        if (target.IsError) return Result<PremisesCorrectionDto>.Failure(target.Errors);
        if (!Matches(target.Value.Version, targetVersion)) return PremisesCorrectionErrors.Stale;
        if (documents.Count is < 1 or > 2) return PremisesCorrectionErrors.Evidence;
        var id = Guid.NewGuid(); var prepared = new List<PreparedLocationEvidence>();
        foreach (var document in documents)
        {
            var read = await storage.ReadAsync(id, document, ct);
            if (read.IsError) return Result<PremisesCorrectionDto>.Failure(read.Errors);
            prepared.Add(read.Value);
        }
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync(targetId, branch, ct)) return PremisesCorrectionErrors.Busy;
            var recheck = await TargetAsync(targetId, branch, actor.Value, ct);
            if (recheck.IsError) return Result<PremisesCorrectionDto>.Failure(recheck.Errors);
            if (!Matches(recheck.Value.Version, targetVersion)) return PremisesCorrectionErrors.Stale;
            if (await db.PremisesCorrections.AnyAsync(x => x.Status == PremisesCorrectionStatus.PendingReview &&
                (branch ? x.BranchId == targetId : x.LocationId == targetId), ct)) return PremisesCorrectionErrors.PendingExists;
            var row = PremisesCorrection.Create(id, branch ? null : targetId, branch ? targetId : null, actor.Value,
                recheck.Value.Details, recheck.Value.Version, normalized.Value, reason, prepared.Select(x => x.Metadata).ToArray());
            if (row.IsError) return Result<PremisesCorrectionDto>.Failure(row.Errors);
            foreach (var document in prepared) { await storage.WriteAsync(document, ct); written.Add(document.Metadata.StorageKey); }
            db.PremisesCorrections.Add(row.Value); await db.SaveChangesAsync(ct);
            commitAttempted = true; await tx.CommitAsync(ct); committed = true;
            return Dto(row.Value);
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 } sql &&
            (sql.Message.Contains("IX_PremisesCorrections_LocationId", StringComparison.Ordinal) || sql.Message.Contains("IX_PremisesCorrections_BranchId", StringComparison.Ordinal)))
        { return PremisesCorrectionErrors.PendingExists; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { logger.LogError("Uncommitted premises evidence cleanup failed with {ExceptionType}.", e.GetType().Name); throw; }
                }
            else if (!committed) logger.LogError("Premises correction commit outcome is uncertain; private evidence retained for reconciliation.");
        }
    }

    private sealed record Target(PremisesDetails Details, byte[] Version);
    private async Task<Result<Target>> TargetAsync(Guid id, bool branch, Guid actor, CancellationToken ct)
    {
        if (branch)
        {
            var row = await db.ClinicBranches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || !await ManagesAsync(actor, row.ClinicId, ct)) return PremisesCorrectionErrors.NotFound;
            if (!row.IsActive) return PremisesCorrectionErrors.NotApproved;
            return new Target(PremisesDetails.From(row), row.Version);
        }
        var place = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == actor, ct);
        if (place is null) return PremisesCorrectionErrors.NotFound;
        if (place.ClinicBranchId.HasValue) return PracticeLocationErrors.ManagedLocation;
        if (!await EligibleDoctorAsync(actor, ct)) return PremisesCorrectionErrors.Forbidden;
        if (place.Status != PracticeLocationStatus.Approved) return PremisesCorrectionErrors.NotApproved;
        return new Target(PremisesDetails.From(place), place.Version);
    }

    public async Task<Result<PremisesCorrectionDto>> ReviewAsync(Guid id, PremisesCorrectionStatus outcome, string reason, string version,
        bool samePremises, bool license, bool authority, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(true, ct);
        if (actor.IsError) return Result<PremisesCorrectionDto>.Failure(actor.Errors);
        var snapshot = await db.PremisesCorrections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (snapshot is null) return PremisesCorrectionErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(snapshot.BranchId ?? snapshot.LocationId!.Value, snapshot.BranchId.HasValue, ct)) return PremisesCorrectionErrors.Busy;
        var row = await db.PremisesCorrections.Include(x => x.Documents).SingleAsync(x => x.Id == id, ct);
        if (row.Status != PremisesCorrectionStatus.PendingReview) return PremisesCorrectionErrors.AlreadyReviewed;
        if (!Matches(row.Version, version)) return PremisesCorrectionErrors.Stale;
        var admin = await ActorAsync(true, ct); if (admin.IsError) return Result<PremisesCorrectionDto>.Failure(admin.Errors);
        if (row.BranchId is Guid branchId)
        {
            var branch = await db.ClinicBranches.SingleAsync(x => x.Id == branchId, ct);
            // A manager who is also Admin cannot independently review their clinic's changes.
            if (await db.ClinicManagementMemberships.AnyAsync(x => x.ClinicId == branch.ClinicId && x.UserId == actor.Value && x.IsActive, ct) ||
                await db.ClinicRegistrationSubmissions.AnyAsync(x => x.Id == branch.SourceSubmissionId && x.ApplicantUserId == actor.Value, ct))
                return PremisesCorrectionErrors.SelfReview;
            if (outcome == PremisesCorrectionStatus.Approved && !await ManagesAsync(row.ApplicantUserId, branch.ClinicId, ct)) return PremisesCorrectionErrors.Forbidden;
            var decision = row.Review(outcome, reason, actor.Value, clock.GetUtcNow(), samePremises, license, authority);
            if (decision.IsError) return Result<PremisesCorrectionDto>.Failure(decision.Errors);
            if (outcome == PremisesCorrectionStatus.Approved)
            {
                var applied = branch.ApplyCorrection(row); if (applied.IsError) return Result<PremisesCorrectionDto>.Failure(applied.Errors);
            }
        }
        else
        {
            var place = await db.DoctorPracticeLocations.SingleAsync(x => x.Id == row.LocationId, ct);
            if (outcome == PremisesCorrectionStatus.Approved && !await EligibleDoctorAsync(place.UserId, ct)) return PremisesCorrectionErrors.Forbidden;
            var decision = row.Review(outcome, reason, actor.Value, clock.GetUtcNow(), samePremises, license, authority);
            if (decision.IsError) return Result<PremisesCorrectionDto>.Failure(decision.Errors);
            if (outcome == PremisesCorrectionStatus.Approved)
            {
                var applied = place.ApplyCorrection(row); if (applied.IsError) return Result<PremisesCorrectionDto>.Failure(applied.Errors);
            }
        }
        try
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row);
        }
        catch (DbUpdateConcurrencyException) { return PremisesCorrectionErrors.Stale; }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 } sql &&
            sql.Message.Contains("IX_ClinicBranches_NormalizedLicensingAuthority_NormalizedLicenseNumber", StringComparison.Ordinal))
        { return PremisesCorrectionErrors.DuplicateLicense; }
    }

    // Evidence is private to the submitting account and Admin, not every manager in the clinic.
    private IQueryable<PremisesCorrection> Visible(Guid actor, bool admin) => db.PremisesCorrections.AsNoTracking().Where(x => admin || x.ApplicantUserId == actor);
    public async Task<Result<PremisesCorrectionPage>> ListAsync(bool admin, Guid? locationId, Guid? branchId, PremisesCorrectionStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PremisesCorrectionPage>.Failure(actor.Errors);
        var query = Visible(actor.Value, admin);
        if (locationId.HasValue) query = query.Where(x => x.LocationId == locationId);
        if (branchId.HasValue) query = query.Where(x => x.BranchId == branchId);
        if (status.HasValue) query = query.Where(x => x.Status == status);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).Include(x => x.Documents).ToArrayAsync(ct);
        return new PremisesCorrectionPage(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    public async Task<Result<PremisesCorrectionDto>> GetAsync(Guid id, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PremisesCorrectionDto>.Failure(actor.Errors);
        var row = await Visible(actor.Value, admin).Include(x => x.Documents).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? PremisesCorrectionErrors.NotFound : Dto(row);
    }
    public async Task<Result<PracticeLocationEvidenceDownload>> DownloadAsync(Guid id, Guid documentId, bool admin, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationEvidenceDownload>.Failure(actor.Errors);
        var row = await db.PremisesCorrectionDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.CorrectionId == id &&
            db.PremisesCorrections.Any(c => c.Id == x.CorrectionId && (admin || c.ApplicantUserId == actor.Value)), ct);
        if (row is null) return PremisesCorrectionErrors.NotFound;
        var opened = await storage.OpenReadAsync(row, ct); if (opened.IsError) return PremisesCorrectionErrors.NotFound;
        try
        {
            db.PremisesCorrectionEvidenceAccesses.Add(PremisesCorrectionEvidenceAccess.Grant(row.Id, actor.Value, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct); return new PracticeLocationEvidenceDownload(opened.Value, row.MediaType, row.OriginalFileName);
        }
        catch { await opened.Value.DisposeAsync(); throw; }
    }
    private void Fresh()
    { if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Premises corrections require a context without pending writes."); }
    private async Task<bool> LockAsync(Guid id, bool branch, CancellationToken ct)
    {
        // Independent corrections share the lock used by fee changes and the original review workflow.
        var resource = branch ? $"ClinIQ.ClinicBranch.{id:N}" : $"ClinIQ.PracticeLocation.{id:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (codes.Single() < -3) throw new InvalidOperationException("Unexpected premises lock failure.");
        return codes.Single() >= 0;
    }
    private static bool Matches(byte[] version, string candidate) => Convert.ToBase64String(version) == candidate;
    private static PremisesCorrectionDto Dto(PremisesCorrection r) => new(r.Id, r.LocationId, r.BranchId, r.ApplicantUserId, r.Original, r.Proposed,
        Convert.ToBase64String(r.OriginalVersion), r.RequestReason, r.Status.ToString(), r.CreatedAtUtc, Convert.ToBase64String(r.Version),
        r.ReviewerUserId, r.ReviewReason, r.ReviewedAtUtc, r.SamePremisesVerified, r.LicenseVerified, r.AuthorityVerified,
        r.Documents.OrderBy(x => x.Kind).Select(x => new CorrectionEvidenceDto(x.Id, x.Kind, x.OriginalFileName, x.MediaType, x.Length)).ToArray());
}
