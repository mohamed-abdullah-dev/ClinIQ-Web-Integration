using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Specialties;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClinIQ.Infrastructure.Specialties;

public sealed class SpecialtyAdministrationService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser, TimeProvider clock)
    : ISpecialtyAdministrationService
{
    private async Task<Result<Guid>> AdminAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return SpecialtyErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed || !await users.IsInRoleAsync(user, "Admin")) return SpecialtyErrors.Forbidden;
        return id;
    }

    public async Task<Result<IReadOnlyList<AdminSpecialtyDto>>> ListAsync(CancellationToken ct)
    {
        var admin = await AdminAsync(ct);
        if (admin.IsError) return Result<IReadOnlyList<AdminSpecialtyDto>>.Failure(admin.Errors);
        var rows = await db.Specialties.AsNoTracking().OrderBy(x => x.NormalizedNameEn).ThenBy(x => x.Id).ToArrayAsync(ct);
        return Result<IReadOnlyList<AdminSpecialtyDto>>.Success(rows.Select(Map).ToArray());
    }

    public async Task<Result<AdminSpecialtyDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var admin = await AdminAsync(ct);
        if (admin.IsError) return Result<AdminSpecialtyDto>.Failure(admin.Errors);
        var row = await db.Specialties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? SpecialtyErrors.NotFound : Map(row);
    }

    public async Task<Result<AdminSpecialtyDto>> CreateAsync(string nameAr, string nameEn, CancellationToken ct)
    {
        EnsureFreshContext();
        var admin = await AdminAsync(ct);
        if (admin.IsError) return Result<AdminSpecialtyDto>.Failure(admin.Errors);
        var created = Specialty.Create(nameAr, nameEn);
        if (created.IsError) return Result<AdminSpecialtyDto>.Failure(created.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var duplicate = await DuplicateAsync(created.Value, null, ct);
        if (duplicate is not null) return duplicate;
        db.Specialties.Add(created.Value);
        db.SpecialtyCatalogChanges.Add(SpecialtyCatalogChange.Created(created.Value, admin.Value, clock.GetUtcNow()));
        var saved = await SaveAsync(transaction, false, ct);
        return saved.IsSuccess ? Map(created.Value) : Result<AdminSpecialtyDto>.Failure(saved.Errors);
    }

    public async Task<Result<AdminSpecialtyDto>> RenameAsync(Guid id, string nameAr, string nameEn, string version, CancellationToken ct)
    {
        EnsureFreshContext();
        var admin = await AdminAsync(ct);
        if (admin.IsError) return Result<AdminSpecialtyDto>.Failure(admin.Errors);
        var requested = Specialty.Create(nameAr, nameEn);
        if (requested.IsError) return Result<AdminSpecialtyDto>.Failure(requested.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(id, ct)) return SpecialtyErrors.Busy;
        var row = await db.Specialties.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return SpecialtyErrors.NotFound;
        if (!MatchesVersion(row, version)) return SpecialtyErrors.Stale;
        var duplicate = await DuplicateAsync(requested.Value, id, ct);
        if (duplicate is not null) return duplicate;
        if (row.NameAr == requested.Value.NameAr && row.NameEn == requested.Value.NameEn) return Map(row);
        var previousAr = row.NameAr; var previousEn = row.NameEn;
        var renamed = row.Rename(requested.Value.NameAr, requested.Value.NameEn);
        if (renamed.IsError) return Result<AdminSpecialtyDto>.Failure(renamed.Errors);
        db.SpecialtyCatalogChanges.Add(SpecialtyCatalogChange.Renamed(row, previousAr, previousEn, admin.Value, clock.GetUtcNow()));
        var saved = await SaveAsync(transaction, false, ct);
        return saved.IsSuccess ? Map(row) : Result<AdminSpecialtyDto>.Failure(saved.Errors);
    }

    public async Task<Result<Success>> DeleteAsync(Guid id, string version, CancellationToken ct)
    {
        EnsureFreshContext();
        var admin = await AdminAsync(ct);
        if (admin.IsError) return Result<Success>.Failure(admin.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(id, ct)) return SpecialtyErrors.Busy;
        var row = await db.Specialties.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return SpecialtyErrors.NotFound;
        if (!MatchesVersion(row, version)) return SpecialtyErrors.Stale;
        if (await db.DoctorRegistrations.AnyAsync(x => x.SpecialtyId == id, ct) ||
            await db.DoctorProfiles.AnyAsync(x => x.SpecialtyId == id, ct) ||
            await db.DoctorProfessionalChanges.AnyAsync(x => x.SpecialtyId == id || x.OriginalSpecialtyId == id, ct)) return SpecialtyErrors.InUse;
        db.SpecialtyCatalogChanges.Add(SpecialtyCatalogChange.Deleted(row, admin.Value, clock.GetUtcNow()));
        db.Specialties.Remove(row);
        return await SaveAsync(transaction, true, ct);
    }

    private async Task<Error?> DuplicateAsync(Specialty requested, Guid? exclude, CancellationToken ct)
    {
        var query = db.Specialties.AsNoTracking().Where(x => exclude == null || x.Id != exclude);
        if (await query.AnyAsync(x => x.NormalizedNameAr == requested.NormalizedNameAr, ct)) return SpecialtyErrors.NameArTaken;
        if (await query.AnyAsync(x => x.NormalizedNameEn == requested.NormalizedNameEn, ct)) return SpecialtyErrors.NameEnTaken;
        return null;
    }

    private async Task<Result<Success>> SaveAsync(IDbContextTransaction transaction, bool deleting, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Success;
        }
        catch (DbUpdateConcurrencyException) { return SpecialtyErrors.Stale; }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql && sql.Number is 2601 or 2627 &&
            (sql.Message.Contains("'IX_Specialties_NormalizedNameAr'", StringComparison.Ordinal) ||
             sql.Message.Contains("'IX_Specialties_NormalizedNameEn'", StringComparison.Ordinal)))
        {
            // The unique indexes are the final guard when separate requests pass the preliminary checks.
            return ((SqlException)error.InnerException!).Message.Contains("'IX_Specialties_NormalizedNameAr'", StringComparison.Ordinal)
                ? SpecialtyErrors.NameArTaken : SpecialtyErrors.NameEnTaken;
        }
        catch (DbUpdateException error) when (deleting && error.InnerException is SqlException { Number: 547 } sql &&
            (sql.Message.Contains("FK_DoctorRegistrations_Specialties_SpecialtyId", StringComparison.Ordinal) ||
             sql.Message.Contains("FK_DoctorProfiles_Specialties_SpecialtyId", StringComparison.Ordinal) ||
             sql.Message.Contains("FK_DoctorProfessionalChanges_Specialties_", StringComparison.Ordinal)))
        {
            // A registration may have linked the specialty after the usage check. The FK still protects it.
            return SpecialtyErrors.InUse;
        }
    }

    private async Task<bool> LockAsync(Guid id, CancellationToken ct)
    {
        var resource = $"ClinIQ.Specialty.{id:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected specialty lock failure.");
        ct.ThrowIfCancellationRequested();
        return codes.Single() >= 0;
    }

    private void EnsureFreshContext()
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Specialty editing requires a context without pending changes.");
    }
    private static bool MatchesVersion(Specialty row, string version) =>
        string.Equals(version, Convert.ToBase64String(row.Version), StringComparison.Ordinal);
    private static AdminSpecialtyDto Map(Specialty row) => new(row.Id, row.NameAr, row.NameEn, Convert.ToBase64String(row.Version));
}
