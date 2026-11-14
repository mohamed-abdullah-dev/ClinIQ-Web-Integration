using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.PracticeLocations;

public sealed class PracticeLocationService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser,
    TimeProvider clock, RegistrationStorage storage, ILogger<PracticeLocationService> logger) : IPracticeLocationService
{
    private async Task<Result<Guid>> ActorAsync(bool admin, CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return PracticeLocationErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed || !await users.IsInRoleAsync(user, admin ? "Admin" : "Doctor") ||
            (!admin && !await EligibleDoctorAsync(id, ct))) return PracticeLocationErrors.Forbidden;
        return id;
    }
    private Task<bool> EligibleDoctorAsync(Guid id, CancellationToken ct) => db.DoctorProfiles.AsNoTracking().AnyAsync(p => p.UserId == id &&
        db.Users.Any(user => user.Id == id && user.EmailConfirmed) &&
        db.UserRoles.Any(role => role.UserId == id && db.Roles.Any(r => r.Id == role.RoleId && r.NormalizedName == "DOCTOR")) &&
        db.DoctorRegistrations.Any(r => r.Id == p.DoctorRegistrationId && r.UserId == id && r.IsCurrent && r.Status == DoctorRegistrationStatus.Approved), ct);

    public async Task<Result<PracticeLocationDto>> CreateAsync(PracticeLocationDraft input, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct);
        if (actor.IsError) return Result<PracticeLocationDto>.Failure(actor.Errors);
        var details = PracticeLocationDetails.Create(input.Name, input.Address, input.Latitude, input.Longitude);
        if (details.IsError) return Result<PracticeLocationDto>.Failure(details.Errors);
        var price = ConsultationPrice.Create(input.ConsultationFee, input.Currency);
        if (price.IsError) return Result<PracticeLocationDto>.Failure(price.Errors);
        var profileId = await db.DoctorProfiles.Where(x => x.UserId == actor.Value).Select(x => x.Id).SingleAsync(ct);
        var row = DoctorPracticeLocation.Create(Guid.NewGuid(), profileId, actor.Value, details.Value, price.Value);
        db.DoctorPracticeLocations.Add(row); await db.SaveChangesAsync(ct); return Dto(row);
    }
    public async Task<Result<PracticeLocationDto>> UpdateDraftAsync(Guid id, PracticeLocationDraft input, string version, CancellationToken ct)
    {
        var details = PracticeLocationDetails.Create(input.Name, input.Address, input.Latitude, input.Longitude);
        if (details.IsError) return Result<PracticeLocationDto>.Failure(details.Errors);
        var price = ConsultationPrice.Create(input.ConsultationFee, input.Currency);
        if (price.IsError) return Result<PracticeLocationDto>.Failure(price.Errors);
        return await EditAsync(id, version, (row, actor) => row.UpdateDraft(actor, details.Value, price.Value), ct);
    }
    public async Task<Result<PracticeLocationDto>> ChangeFeeAsync(Guid id, decimal amount, string currency, string version, CancellationToken ct)
    {
        var price = ConsultationPrice.Create(amount, currency);
        if (price.IsError) return Result<PracticeLocationDto>.Failure(price.Errors);
        return await EditAsync(id, version, (row, actor) => row.ChangeFee(actor, price.Value), ct);
    }
    private async Task<Result<PracticeLocationDto>> EditAsync(Guid id, string version, Func<DoctorPracticeLocation, Guid, Result<Success>> edit, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct);
        if (actor.IsError) return Result<PracticeLocationDto>.Failure(actor.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(id, ct)) return PracticeLocationErrors.Busy;
        var row = await db.DoctorPracticeLocations.SingleOrDefaultAsync(x => x.Id == id && x.UserId == actor.Value, ct);
        if (row is null) return PracticeLocationErrors.NotFound;
        if (!Matches(row, version)) return PracticeLocationErrors.Stale;
        var rechecked = await ActorAsync(false, ct); if (rechecked.IsError) return Result<PracticeLocationDto>.Failure(rechecked.Errors);
        var changed = edit(row, actor.Value); if (changed.IsError) return Result<PracticeLocationDto>.Failure(changed.Errors);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Dto(row); }
        catch (DbUpdateConcurrencyException) { return PracticeLocationErrors.Stale; }
    }
    public async Task<Result<PracticeLocationDto>> SubmitAsync(Guid id, string version, IReadOnlyList<PracticeLocationUpload> documents, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<PracticeLocationDto>.Failure(actor.Errors);
        var snapshot = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == actor.Value, ct);
        if (snapshot is null) return PracticeLocationErrors.NotFound;
        if (!Matches(snapshot, version)) return PracticeLocationErrors.Stale;
        if (documents.Count is < 1 or > 2) return PracticeLocationErrors.RequiredEvidence;
        var submissionId = Guid.NewGuid(); var prepared = new List<PreparedLocationEvidence>();
        foreach (var document in documents)
        {
            var read = await storage.ReadAsync(submissionId, document, ct);
            if (read.IsError) return Result<PracticeLocationDto>.Failure(read.Errors); prepared.Add(read.Value);
        }
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync(id, ct)) return PracticeLocationErrors.Busy;
            var row = await db.DoctorPracticeLocations.SingleAsync(x => x.Id == id && x.UserId == actor.Value, ct);
            if (!Matches(row, version)) return PracticeLocationErrors.Stale;
            var rechecked = await ActorAsync(false, ct); if (rechecked.IsError) return Result<PracticeLocationDto>.Failure(rechecked.Errors);
            var submission = row.Submit(submissionId, actor.Value, prepared.Select(x => x.Metadata).ToArray());
            if (submission.IsError) return Result<PracticeLocationDto>.Failure(submission.Errors);
            foreach (var document in prepared) { await storage.WriteAsync(document, ct); written.Add(document.Metadata.StorageKey); }
            db.PracticeLocationSubmissions.Add(submission.Value); await db.SaveChangesAsync(ct);
            commitAttempted = true; await transaction.CommitAsync(ct); committed = true; return Dto(row);
        }
        catch (DbUpdateConcurrencyException) { return PracticeLocationErrors.Stale; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { logger.LogError("Uncommitted location evidence cleanup failed with {ExceptionType}.", error.GetType().Name); throw; }
                }
            else if (!committed) logger.LogError("Location submission commit outcome is uncertain; private evidence retained for reconciliation.");
        }
    }
    public async Task<Result<PracticeLocationDto>> ReviewAsync(Guid id, Guid submissionId, PracticeLocationStatus outcome, string reason,
        string version, bool licenseMatchesAddress, bool doctorWorkLinkVerified, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<PracticeLocationDto>.Failure(actor.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(id, ct)) return PracticeLocationErrors.Busy;
        var row = await db.DoctorPracticeLocations.SingleOrDefaultAsync(x => x.Id == id, ct);
        var submission = await db.PracticeLocationSubmissions.SingleOrDefaultAsync(x => x.Id == submissionId && x.LocationId == id, ct);
        if (row is null || submission is null) return PracticeLocationErrors.NotFound;
        if (!Matches(row, version)) return PracticeLocationErrors.Stale;
        if (outcome == PracticeLocationStatus.Approved && !await EligibleDoctorAsync(row.UserId, ct)) return PracticeLocationErrors.Forbidden;
        var decision = row.Review(submission, outcome, reason, actor.Value, clock.GetUtcNow(), licenseMatchesAddress, doctorWorkLinkVerified);
        if (decision.IsError) return Result<PracticeLocationDto>.Failure(decision.Errors);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Dto(row); }
        catch (DbUpdateConcurrencyException) { return PracticeLocationErrors.Stale; }
    }
    public async Task<Result<PracticeLocationPage>> ListAsync(bool admin, int page, int pageSize, PracticeLocationStatus? status, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationPage>.Failure(actor.Errors);
        var query = db.DoctorPracticeLocations.AsNoTracking().Where(x => admin || x.UserId == actor.Value);
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        var branchIds = rows.Where(x => x.ClinicBranchId.HasValue).Select(x => x.ClinicBranchId!.Value).ToArray();
        var branches = await db.ClinicBranches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).Select(b => new
        {
            Branch = b,
            ClinicActive = db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive)
        }).ToDictionaryAsync(b => b.Branch.Id, ct);
        return new PracticeLocationPage(rows.Select(row => row.ClinicBranchId is Guid id && branches.TryGetValue(id, out var branch)
            ? Dto(row, branch.Branch, branch.ClinicActive) : Dto(row)).ToArray(), total, page, pageSize);
    }
    public async Task<Result<PracticeLocationDetail>> GetAsync(Guid id, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationDetail>.Failure(actor.Errors);
        var row = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (admin || x.UserId == actor.Value), ct);
        if (row is null) return PracticeLocationErrors.NotFound;
        var submission = row.CurrentSubmissionId is Guid submissionId ? await db.PracticeLocationSubmissions.AsNoTracking().Include(x => x.Documents)
            .SingleAsync(x => x.Id == submissionId && x.LocationId == id, ct) : null;
        var branch = row.ClinicBranchId is Guid branchId ? await db.ClinicBranches.AsNoTracking().SingleAsync(b => b.Id == branchId, ct) : null;
        var active = branch is null || await db.Clinics.AnyAsync(c => c.Id == branch.ClinicId && c.IsActive, ct);
        return new PracticeLocationDetail(Dto(row, branch, active), submission is null ? null : Dto(submission));
    }
    public async Task<Result<PracticeLocationSubmissionPage>> ListSubmissionsAsync(Guid id, bool admin, int page, int pageSize, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationSubmissionPage>.Failure(actor.Errors);
        if (!await db.DoctorPracticeLocations.AnyAsync(x => x.Id == id && (admin || x.UserId == actor.Value), ct)) return PracticeLocationErrors.NotFound;
        var query = db.PracticeLocationSubmissions.AsNoTracking().Where(x => x.LocationId == id);
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.AttemptNumber).Skip(checked((page - 1) * pageSize)).Take(pageSize)
            .Include(x => x.Documents).ToArrayAsync(ct);
        return new PracticeLocationSubmissionPage(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    public async Task<Result<PracticeLocationSubmissionDto>> GetSubmissionAsync(Guid id, Guid submissionId, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationSubmissionDto>.Failure(actor.Errors);
        var row = await db.PracticeLocationSubmissions.AsNoTracking().Include(x => x.Documents).SingleOrDefaultAsync(x => x.Id == submissionId && x.LocationId == id &&
            db.DoctorPracticeLocations.Any(location => location.Id == id && (admin || location.UserId == actor.Value)), ct);
        return row is null ? PracticeLocationErrors.NotFound : Dto(row);
    }
    public async Task<Result<PracticeLocationEvidenceDownload>> DownloadAsync(Guid id, Guid submissionId, Guid documentId, bool admin, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<PracticeLocationEvidenceDownload>.Failure(actor.Errors);
        var row = await db.PracticeLocationDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.SubmissionId == submissionId &&
            db.PracticeLocationSubmissions.Any(s => s.Id == x.SubmissionId && s.LocationId == id &&
                db.DoctorPracticeLocations.Any(location => location.Id == id && (admin || location.UserId == actor.Value))), ct);
        if (row is null) return PracticeLocationErrors.NotFound;
        var opened = await storage.OpenReadAsync(row, ct); if (opened.IsError) return PracticeLocationErrors.NotFound;
        try
        {
            db.PracticeLocationEvidenceAccesses.Add(PracticeLocationEvidenceAccess.Grant(documentId, actor.Value, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct); return new PracticeLocationEvidenceDownload(opened.Value, row.MediaType, row.OriginalFileName);
        }
        catch { await opened.Value.DisposeAsync(); throw; }
    }
    private void Fresh() { if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Location operations require a context without pending writes."); }
    private async Task<bool> LockAsync(Guid id, CancellationToken ct)
    {
        var resource = $"ClinIQ.PracticeLocation.{id:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (codes.Single() < -3) throw new InvalidOperationException("Unexpected location lock failure."); return codes.Single() >= 0;
    }
    private static bool Matches(DoctorPracticeLocation row, string version) => Convert.ToBase64String(row.Version) == version;
    private static PracticeLocationDto Dto(DoctorPracticeLocation row, ClinIQ.Domain.Clinics.ClinicBranch? branch = null, bool clinicActive = true) => new(row.Id, row.DoctorProfileId, row.UserId,
        branch?.Name ?? row.Name, branch?.Address ?? row.Address, branch is null ? row.Latitude : branch.Latitude, branch is null ? row.Longitude : branch.Longitude,
        row.ConsultationFee, row.Currency, row.Status.ToString(), row.AttemptNumber, row.CurrentSubmissionId, row.CreatedAtUtc, Convert.ToBase64String(row.Version), row.ClinicBranchId,
        row.IsActive && (!row.ClinicBranchId.HasValue || (branch is not null && branch.IsActive && clinicActive)));
    private static PracticeLocationSubmissionDto Dto(PracticeLocationSubmission row) => new(row.Id, row.LocationId, row.AttemptNumber, row.Name, row.Address,
        row.Latitude, row.Longitude, row.ConsultationFee, row.Currency, row.Status.ToString(), row.CreatedAtUtc, row.ReviewerUserId, row.ReviewReason, row.ReviewedAtUtc,
        row.LicenseMatchesAddress, row.DoctorWorkLinkVerified, row.Documents.OrderBy(x => x.Kind)
            .Select(x => new PracticeLocationEvidenceDto(x.Id, x.Kind, x.OriginalFileName, x.MediaType, x.Length)).ToArray());
}
