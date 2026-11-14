using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class DoctorReviewService(AppDbContext db, UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles, ICurrentUser currentUser, TimeProvider clock, RegistrationStorage storage)
    : IDoctorReviewService
{
    private async Task<Guid?> ReviewerAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        return user is not null && user.EmailConfirmed && await users.IsInRoleAsync(user, "Admin") ? id : null;
    }

    public async Task<Result<DoctorRegistrationPage>> ListAsync(int page, int pageSize, DoctorRegistrationStatus? status, CancellationToken ct)
    {
        if (await ReviewerAsync(ct) is null) return DoctorReviewErrors.Forbidden;
        var query = db.DoctorRegistrations.AsNoTracking();
        if (status is not null) query = query.Where(row => row.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await (from row in query
                          join specialty in db.Specialties on row.SpecialtyId equals specialty.Id
                          orderby row.CreatedAtUtc, row.Id
                          select new { Registration = row, specialty.NameAr, specialty.NameEn })
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new DoctorRegistrationPage(rows.Select(row => Summary(row.Registration, row.NameAr, row.NameEn)).ToArray(), total, page, pageSize);
    }

    public async Task<Result<DoctorRegistrationDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        if (await ReviewerAsync(ct) is null) return DoctorReviewErrors.Forbidden;
        var row = await db.DoctorRegistrations.AsNoTracking().Include(row => row.Documents).SingleOrDefaultAsync(row => row.Id == id, ct);
        if (row is null) return DoctorReviewErrors.NotFound;
        var user = await db.Users.AsNoTracking().Where(user => user.Id == row.UserId).Select(user => new { user.Email, user.EmailConfirmed }).SingleAsync(ct);
        var specialty = await db.Specialties.AsNoTracking().SingleAsync(specialty => specialty.Id == row.SpecialtyId, ct);
        var review = await db.DoctorRegistrationReviews.AsNoTracking().SingleOrDefaultAsync(review => review.DoctorRegistrationId == id, ct);
        var profileId = await db.DoctorProfiles.Where(profile => profile.DoctorRegistrationId == id).Select(profile => (Guid?)profile.Id).SingleOrDefaultAsync(ct);
        return new DoctorRegistrationDetail(Summary(row, specialty.NameAr, specialty.NameEn), user.Email!, user.EmailConfirmed,
            row.PhoneNumber, row.RegistrationNumber, row.Documents.OrderBy(document => document.Kind)
                .Select(document => new DoctorEvidenceDto(document.Id, document.Kind, document.OriginalFileName, document.MediaType, document.Length)).ToArray(),
            review is null ? null : new DoctorDecisionDto(review.ReviewerUserId, review.Outcome.ToString(), review.Reason, review.ReviewedAtUtc),
            profileId, Convert.ToBase64String(row.Version));
    }

    public async Task<Result<DoctorEvidenceDownload>> DownloadAsync(Guid id, Guid documentId, CancellationToken ct)
    {
        if (await ReviewerAsync(ct) is not Guid reviewerId) return DoctorReviewErrors.Forbidden;
        var document = await db.Set<DoctorEvidenceDocument>().AsNoTracking()
            .SingleOrDefaultAsync(document => document.Id == documentId && document.DoctorRegistrationId == id, ct);
        if (document is null) return DoctorReviewErrors.NotFound;
        var opened = await storage.OpenReadAsync(document, ct);
        if (opened.IsError) return Result<DoctorEvidenceDownload>.Failure(opened.Errors);
        var stream = opened.Value;
        try
        {
            db.DoctorEvidenceAccesses.Add(DoctorEvidenceAccess.Grant(document.Id, reviewerId, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return new DoctorEvidenceDownload(stream, document.MediaType, document.OriginalFileName);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    public async Task<Result<DoctorReviewReceipt>> ReviewAsync(Guid id, bool approve, string reason, string version, CancellationToken ct)
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Review requires a context without pending changes.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await ReviewLockedAsync(id, approve, reason, version, ct);
            if (result.IsSuccess) await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException) { return DoctorReviewErrors.Stale; }
    }

    private async Task<Result<DoctorReviewReceipt>> ReviewLockedAsync(Guid id, bool approve, string reason, string version, CancellationToken ct)
    {
        if (await ReviewerAsync(ct) is not Guid reviewerId) return DoctorReviewErrors.Forbidden;
        if (!await LockAsync($"ClinIQ.DoctorReview.{id:N}", ct)) return DoctorReviewErrors.Busy;
        var row = await db.DoctorRegistrations.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (row is null) return DoctorReviewErrors.NotFound;
        if (!row.IsCurrent || row.Status != DoctorRegistrationStatus.PendingReview) return DoctorReviewErrors.AlreadyReviewed;
        if (!row.Version.AsSpan().SequenceEqual(Convert.FromBase64String(version))) return DoctorReviewErrors.Stale;
        var reviewed = row.Review(approve, reason, reviewerId, clock.GetUtcNow());
        if (reviewed.IsError) return Result<DoctorReviewReceipt>.Failure(reviewed.Errors);
        db.DoctorRegistrationReviews.Add(reviewed.Value);
        DoctorProfile? profile = null;
        if (approve)
        {
            profile = DoctorProfile.FromApprovedRegistration(row).Value;
            db.DoctorProfiles.Add(profile);
            if (!await roles.RoleExistsAsync("Doctor"))
            {
                if (!await LockAsync("ClinIQ.Identity.Role.Doctor", ct)) return DoctorReviewErrors.Busy;
                if (!await roles.RoleExistsAsync("Doctor"))
                {
                    var created = await roles.CreateAsync(new IdentityRole<Guid>("Doctor") { Id = Guid.NewGuid() });
                    if (!created.Succeeded) throw new InvalidOperationException("Doctor role provisioning failed.");
                }
            }
            var user = await users.FindByIdAsync(row.UserId.ToString()) ?? throw new InvalidOperationException("Registration account is missing.");
            if (!await users.IsInRoleAsync(user, "Doctor"))
            {
                var assigned = await users.AddToRoleAsync(user, "Doctor");
                if (!assigned.Succeeded) throw new InvalidOperationException("Doctor role assignment failed.");
            }
        }
        await db.SaveChangesAsync(ct);
        return new DoctorReviewReceipt(row.Id, row.Status.ToString(), profile?.Id, Convert.ToBase64String(row.Version));
    }

    private async Task<bool> LockAsync(string resource, CancellationToken ct)
    {
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected doctor-review lock failure.");
        ct.ThrowIfCancellationRequested();
        return codes.Single() >= 0;
    }
    private static DoctorRegistrationSummary Summary(DoctorRegistration row, string nameAr, string nameEn) =>
        new(row.Id, row.UserId, row.ProfessionalName, row.SpecialtyId, nameAr, nameEn, row.ProfessionalGrade, row.Status.ToString(), row.CreatedAtUtc,
            row.IsCurrent, row.AttemptNumber, row.PreviousRegistrationId);
}
