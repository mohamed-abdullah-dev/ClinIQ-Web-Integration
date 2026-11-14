using System.Security.Cryptography;
using System.Text;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Clinics;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Clinics;

public sealed class ClinicService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser, TimeProvider clock,
    RegistrationStorage storage, ILogger<ClinicService> logger, RoleManager<IdentityRole<Guid>> roles) : IClinicService
{
    private async Task<Result<Guid>> ActorAsync(bool admin, CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return ClinicErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed || (admin && !await users.IsInRoleAsync(user, "Admin"))) return ClinicErrors.Forbidden;
        return id;
    }
    private async Task<bool> IsManagerAsync(Guid user, CancellationToken ct)
    {
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user && x.EmailConfirmed, ct);
        return account is not null && await users.IsInRoleAsync(account, "ClinicManager");
    }
    private Task<bool> MemberAsync(Guid clinic, Guid user, CancellationToken ct) => db.ClinicManagementMemberships.AnyAsync(x =>
        x.ClinicId == clinic && x.UserId == user && x.IsActive && db.Clinics.Any(c => c.Id == clinic && c.IsActive), ct);
    private async Task<Result<ClinicApplicationData>> ResolveDataAsync(ClinicApplicationData input, Guid? target, Guid user, CancellationToken ct)
    {
        if (target is not Guid clinicId) return input.Normalize();
        if (!await IsManagerAsync(user, ct)) return ClinicErrors.Forbidden;
        if (!await MemberAsync(clinicId, user, ct)) return ClinicErrors.NotFound;
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(x => x.Id == clinicId, ct);
        return (input with { ClinicName = clinic.Name, Description = clinic.Description }).Normalize();
    }
    public async Task<Result<ClinicApplicationDto>> CreateAsync(ClinicApplicationData input, Guid? targetClinicId, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<ClinicApplicationDto>.Failure(actor.Errors);
        var data = await ResolveDataAsync(input, targetClinicId, actor.Value, ct); if (data.IsError) return Result<ClinicApplicationDto>.Failure(data.Errors);
        var row = ClinicRegistrationRequest.Create(Guid.NewGuid(), actor.Value, targetClinicId, data.Value).Value;
        db.ClinicRegistrationRequests.Add(row); await db.SaveChangesAsync(ct); return Dto(row);
    }
    public async Task<Result<ClinicApplicationDto>> UpdateDraftAsync(Guid id, ClinicApplicationData input, string version, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<ClinicApplicationDto>.Failure(actor.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync($"ClinIQ.ClinicApplication.{id:N}", ct)) return ClinicErrors.Busy;
        var row = await db.ClinicRegistrationRequests.SingleOrDefaultAsync(x => x.Id == id && x.ApplicantUserId == actor.Value, ct);
        if (row is null) return ClinicErrors.NotFound; if (!Matches(row, version)) return ClinicErrors.Stale;
        var data = await ResolveDataAsync(input, row.TargetClinicId, actor.Value, ct); if (data.IsError) return Result<ClinicApplicationDto>.Failure(data.Errors);
        var changed = row.UpdateDraft(actor.Value, data.Value); if (changed.IsError) return Result<ClinicApplicationDto>.Failure(changed.Errors);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Dto(row); }
        catch (DbUpdateConcurrencyException) { return ClinicErrors.Stale; }
    }
    public async Task<Result<ClinicApplicationDto>> SubmitAsync(Guid id, string version, IReadOnlyList<ClinicEvidenceUpload> documents, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<ClinicApplicationDto>.Failure(actor.Errors);
        var snapshot = await db.ClinicRegistrationRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ApplicantUserId == actor.Value, ct);
        if (snapshot is null) return ClinicErrors.NotFound; if (!Matches(snapshot, version)) return ClinicErrors.Stale;
        if (documents.Count is < 2 or > 3) return ClinicErrors.RequiredEvidence;
        var submissionId = Guid.NewGuid(); var prepared = new List<PreparedClinicEvidence>();
        foreach (var upload in documents)
        { var read = await storage.ReadAsync(submissionId, upload, ct); if (read.IsError) return Result<ClinicApplicationDto>.Failure(read.Errors); prepared.Add(read.Value); }
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync($"ClinIQ.ClinicApplication.{id:N}", ct)) return ClinicErrors.Busy;
            var row = await db.ClinicRegistrationRequests.SingleAsync(x => x.Id == id && x.ApplicantUserId == actor.Value, ct);
            if (!Matches(row, version)) return ClinicErrors.Stale;
            var data = await ResolveDataAsync(row.Details, row.TargetClinicId, actor.Value, ct); if (data.IsError) return Result<ClinicApplicationDto>.Failure(data.Errors);
            var refreshed = row.UpdateDraft(actor.Value, data.Value); if (refreshed.IsError) return Result<ClinicApplicationDto>.Failure(refreshed.Errors);
            var submission = row.Submit(submissionId, actor.Value, prepared.Select(x => x.Metadata).ToArray());
            if (submission.IsError) return Result<ClinicApplicationDto>.Failure(submission.Errors);
            foreach (var file in prepared) { await storage.WriteAsync(file, ct); written.Add(file.Metadata.StorageKey); }
            db.ClinicRegistrationSubmissions.Add(submission.Value); await db.SaveChangesAsync(ct);
            commitAttempted = true; await transaction.CommitAsync(ct); committed = true; return Dto(row);
        }
        catch (DbUpdateConcurrencyException) { return ClinicErrors.Stale; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                { try { storage.DeleteCreatedEvidence(key); } catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { logger.LogError("Uncommitted clinic evidence cleanup failed with {ExceptionType}.", e.GetType().Name); throw; } }
            else if (!committed) logger.LogError("Clinic submission commit outcome is uncertain; private evidence retained for reconciliation.");
        }
    }
    public async Task<Result<ClinicApplicationDto>> ReviewAsync(Guid id, Guid submissionId, ClinicApplicationStatus outcome, string reason, string version,
        bool licenseDetailsVerified, bool identityVerified, bool authorityVerified, bool independentlyCorroborated, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<ClinicApplicationDto>.Failure(actor.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync($"ClinIQ.ClinicApplication.{id:N}", ct)) return ClinicErrors.Busy;
        var row = await db.ClinicRegistrationRequests.SingleOrDefaultAsync(x => x.Id == id, ct);
        var submission = await db.ClinicRegistrationSubmissions.SingleOrDefaultAsync(x => x.Id == submissionId && x.RequestId == id, ct);
        if (row is null || submission is null) return ClinicErrors.NotFound; if (!Matches(row, version)) return ClinicErrors.Stale;
        var clinicId = row.TargetClinicId ?? Guid.NewGuid(); var branchId = Guid.NewGuid();
        if (outcome == ClinicApplicationStatus.Approved)
        {
            var applicant = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ApplicantUserId && x.EmailConfirmed, ct);
            if (applicant is null) return ClinicErrors.Forbidden;
            if (row.TargetClinicId is Guid target)
            {
                if (!await LockAsync($"ClinIQ.Clinic.{target:N}", ct)) return ClinicErrors.Busy;
                if (!await IsManagerAsync(row.ApplicantUserId, ct) || !await MemberAsync(target, row.ApplicantUserId, ct)) return ClinicErrors.Forbidden;
            }
            var authority = ClinicApplicationData.LicenseKey(submission.LicensingAuthority); var number = ClinicApplicationData.LicenseKey(submission.LicenseNumber);
            var licenseResource = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority + "\0" + number)));
            if (!await LockAsync($"ClinIQ.FacilityLicense.{licenseResource}", ct)) return ClinicErrors.Busy;
            if (await db.ClinicBranches.AnyAsync(x => x.NormalizedLicensingAuthority == authority && x.NormalizedLicenseNumber == number, ct)) return ClinicErrors.ExistingFacility;
        }
        var decision = row.Review(submission, outcome, reason, actor.Value, clock.GetUtcNow(), licenseDetailsVerified, identityVerified, authorityVerified,
            independentlyCorroborated, outcome == ClinicApplicationStatus.Approved ? clinicId : null, outcome == ClinicApplicationStatus.Approved ? branchId : null);
        if (decision.IsError) return Result<ClinicApplicationDto>.Failure(decision.Errors);
        if (outcome == ClinicApplicationStatus.Approved)
        {
            if (row.TargetClinicId is null)
            {
                db.Clinics.Add(Clinic.FromApprovedSubmission(clinicId, submission).Value);
                db.ClinicManagementMemberships.Add(ClinicManagementMembership.FromApprovedSubmission(clinicId, submission).Value);
            }
            db.ClinicBranches.Add(ClinicBranch.FromApprovedSubmission(branchId, clinicId, submission).Value);
        }
        try
        {
            if (outcome == ClinicApplicationStatus.Approved && row.TargetClinicId is null)
            {
                if (!await new ClinicRoleProvisioner(db, roles).EnsureAsync("ClinicManager", ct)) return ClinicErrors.Busy;
                var user = await users.FindByIdAsync(row.ApplicantUserId.ToString()) ?? throw new InvalidOperationException("Applicant account is missing.");
                if (!await users.IsInRoleAsync(user, "ClinicManager") && !(await users.AddToRoleAsync(user, "ClinicManager")).Succeeded)
                    throw new InvalidOperationException("Clinic-manager role assignment failed.");
            }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Dto(row);
        }
        catch (DbUpdateConcurrencyException) { return ClinicErrors.Stale; }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 } sql &&
            sql.Message.Contains("'IX_ClinicBranches_NormalizedLicensingAuthority_NormalizedLicenseNumber'", StringComparison.Ordinal)) { return ClinicErrors.ExistingFacility; }
    }
    public async Task<Result<ClinicApplicationPage>> ListAsync(bool admin, int page, int pageSize, ClinicApplicationStatus? status, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<ClinicApplicationPage>.Failure(actor.Errors);
        var query = db.ClinicRegistrationRequests.AsNoTracking().Where(x => admin || x.ApplicantUserId == actor.Value);
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new ClinicApplicationPage(rows.Select(Dto).ToArray(), total, page, pageSize);
    }
    public async Task<Result<ClinicApplicationDetail>> GetAsync(Guid id, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<ClinicApplicationDetail>.Failure(actor.Errors);
        var row = await db.ClinicRegistrationRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (admin || x.ApplicantUserId == actor.Value), ct);
        if (row is null) return ClinicErrors.NotFound;
        var sub = row.CurrentSubmissionId is Guid sid ? await db.ClinicRegistrationSubmissions.AsNoTracking().Include(x => x.Documents).SingleAsync(x => x.Id == sid && x.RequestId == id, ct) : null;
        return new ClinicApplicationDetail(Dto(row), sub is null ? null : Dto(sub));
    }
    public async Task<Result<ClinicSubmissionPage>> ListSubmissionsAsync(Guid id, bool admin, int page, int pageSize, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<ClinicSubmissionPage>.Failure(actor.Errors);
        if (!await db.ClinicRegistrationRequests.AnyAsync(x => x.Id == id && (admin || x.ApplicantUserId == actor.Value), ct)) return ClinicErrors.NotFound;
        var query = db.ClinicRegistrationSubmissions.AsNoTracking().Where(x => x.RequestId == id); var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.AttemptNumber).Skip(checked((page - 1) * pageSize)).Take(pageSize).Include(x => x.Documents).ToArrayAsync(ct);
        return new ClinicSubmissionPage(rows.Select(Dto).ToArray(), total, page, pageSize);
    }
    public async Task<Result<ClinicSubmissionDto>> GetSubmissionAsync(Guid id, Guid submissionId, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<ClinicSubmissionDto>.Failure(actor.Errors);
        var row = await db.ClinicRegistrationSubmissions.AsNoTracking().Include(x => x.Documents).SingleOrDefaultAsync(x => x.Id == submissionId && x.RequestId == id &&
            db.ClinicRegistrationRequests.Any(r => r.Id == id && (admin || r.ApplicantUserId == actor.Value)), ct);
        return row is null ? ClinicErrors.NotFound : Dto(row);
    }
    public async Task<Result<ClinicEvidenceDownload>> DownloadAsync(Guid id, Guid submissionId, Guid documentId, bool admin, CancellationToken ct)
    {
        Fresh(); var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<ClinicEvidenceDownload>.Failure(actor.Errors);
        var doc = await db.ClinicRegistrationDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.SubmissionId == submissionId &&
            db.ClinicRegistrationSubmissions.Any(s => s.Id == x.SubmissionId && s.RequestId == id &&
                db.ClinicRegistrationRequests.Any(r => r.Id == id && (admin || r.ApplicantUserId == actor.Value))), ct);
        if (doc is null) return ClinicErrors.NotFound; var read = await storage.OpenReadAsync(doc, ct); if (read.IsError) return ClinicErrors.NotFound;
        try { db.ClinicEvidenceAccesses.Add(ClinicEvidenceAccess.Grant(documentId, actor.Value, clock.GetUtcNow())); await db.SaveChangesAsync(ct); return new ClinicEvidenceDownload(read.Value, doc.MediaType, doc.OriginalFileName); }
        catch { await read.Value.DisposeAsync(); throw; }
    }
    public async Task<Result<ManagedClinicPage>> ListManagedAsync(int page, int pageSize, CancellationToken ct)
    {
        var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<ManagedClinicPage>.Failure(actor.Errors);
        if (!await IsManagerAsync(actor.Value, ct)) return ClinicErrors.Forbidden;
        var query = db.Clinics.AsNoTracking().Where(x => x.IsActive && db.ClinicManagementMemberships.Any(m => m.ClinicId == x.Id && m.UserId == actor.Value && m.IsActive));
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new ManagedClinicPage(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    public async Task<Result<ManagedClinicDto>> GetManagedAsync(Guid id, CancellationToken ct)
    {
        var actor = await ManagerAsync(id, ct); if (actor.IsError) return Result<ManagedClinicDto>.Failure(actor.Errors);
        return Dto(await db.Clinics.AsNoTracking().SingleAsync(x => x.Id == id, ct));
    }
    public async Task<Result<ManagedBranchPage>> ListBranchesAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        var actor = await ManagerAsync(id, ct); if (actor.IsError) return Result<ManagedBranchPage>.Failure(actor.Errors);
        var query = db.ClinicBranches.AsNoTracking().Where(x => x.ClinicId == id);
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new ManagedBranchPage(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    public async Task<Result<ManagedBranchDto>> GetBranchAsync(Guid id, Guid branchId, CancellationToken ct)
    {
        var actor = await ManagerAsync(id, ct); if (actor.IsError) return Result<ManagedBranchDto>.Failure(actor.Errors);
        var row = await db.ClinicBranches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == branchId && x.ClinicId == id, ct);
        return row is null ? ClinicErrors.NotFound : Dto(row);
    }
    private async Task<Result<Guid>> ManagerAsync(Guid clinic, CancellationToken ct)
    {
        var actor = await ActorAsync(false, ct); if (actor.IsError) return actor;
        if (!await IsManagerAsync(actor.Value, ct)) return ClinicErrors.Forbidden;
        return await MemberAsync(clinic, actor.Value, ct) ? actor : ClinicErrors.NotFound;
    }
    private void Fresh() { if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Clinic operations require a context without pending writes."); }
    private async Task<bool> LockAsync(string resource, CancellationToken ct)
    {
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (codes.Single() < -3) throw new InvalidOperationException("Unexpected clinic lock failure."); return codes.Single() >= 0;
    }
    private static bool Matches(ClinicRegistrationRequest row, string version) => Convert.ToBase64String(row.Version) == version;
    private static ClinicApplicationDto Dto(ClinicRegistrationRequest r) => new(r.Id,r.ApplicantUserId,r.TargetClinicId,r.Details,r.Status.ToString(),r.AttemptNumber,r.CurrentSubmissionId,r.ApprovedClinicId,r.ApprovedBranchId,r.CreatedAtUtc,Convert.ToBase64String(r.Version));
    private static ClinicSubmissionDto Dto(ClinicRegistrationSubmission s) => new(s.Id,s.RequestId,s.AttemptNumber,s.Details,s.Status.ToString(),s.CreatedAtUtc,s.ReviewerUserId,s.ReviewReason,s.ReviewedAtUtc,
        s.LicenseDetailsVerified,s.IdentityVerified,s.AuthorityVerified,s.IndependentlyCorroborated,s.Documents.OrderBy(x=>x.Kind).Select(x=>new ClinicEvidenceDto(x.Id,x.Kind,x.OriginalFileName,x.MediaType,x.Length)).ToArray());
    private static ManagedClinicDto Dto(Clinic r) => new(r.Id,r.Name,r.Description,r.IsActive,Convert.ToBase64String(r.Version));
    private static ManagedBranchDto Dto(ClinicBranch r) => new(r.Id,r.ClinicId,r.Name,r.Address,r.Latitude,r.Longitude,r.PublicPhone,r.TimeZoneId,r.IsActive,Convert.ToBase64String(r.Version));
}
