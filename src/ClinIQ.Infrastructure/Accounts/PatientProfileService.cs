using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Patients;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Patients;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class PatientProfileService(AppDbContext db, UserManager<AppUser> users, ICurrentUser currentUser)
    : IPatientProfileService
{
    private async Task<Result<Guid>> OwnerAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return PatientProfileErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !user.EmailConfirmed || !await users.IsInRoleAsync(user, "Patient"))
            return PatientProfileErrors.Forbidden;
        return id;
    }

    public async Task<Result<PatientProfileDto>> GetOwnAsync(CancellationToken ct)
    {
        var owner = await OwnerAsync(ct);
        if (owner.IsError) return Result<PatientProfileDto>.Failure(owner.Errors);
        var profile = await db.PatientProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == owner.Value, ct);
        return profile is null ? PatientProfileErrors.NotFound : Map(profile);
    }

    public async Task<Result<PatientProfileSaveResult>> SaveOwnAsync(string fullName, string phoneNumber, string? version, CancellationToken ct)
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Profile editing requires a context without pending changes.");
        var owner = await OwnerAsync(ct);
        if (owner.IsError) return Result<PatientProfileSaveResult>.Failure(owner.Errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = $"ClinIQ.PatientProfile.{owner.Value:N}";
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected patient-profile lock failure.");
        ct.ThrowIfCancellationRequested();
        if (codes.Single() < 0) return PatientProfileErrors.Busy;

        var profile = await db.PatientProfiles.SingleOrDefaultAsync(x => x.UserId == owner.Value, ct);
        var created = profile is null;
        if (profile is null)
        {
            if (version is not null) return PatientProfileErrors.Stale;
            var result = PatientProfile.Create(owner.Value, fullName, phoneNumber);
            if (result.IsError) return Result<PatientProfileSaveResult>.Failure(result.Errors);
            profile = result.Value;
            db.PatientProfiles.Add(profile);
        }
        else
        {
            if (version is null || !string.Equals(version, Convert.ToBase64String(profile.Version), StringComparison.Ordinal))
                return PatientProfileErrors.Stale;
            var updated = profile.UpdateContact(fullName, phoneNumber);
            if (updated.IsError) return Result<PatientProfileSaveResult>.Failure(updated.Errors);
        }
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new PatientProfileSaveResult(Map(profile), created);
        }
        catch (DbUpdateConcurrencyException) { return PatientProfileErrors.Stale; }
    }

    private static PatientProfileDto Map(PatientProfile profile) =>
        new(profile.Id, profile.UserId, profile.FullName, profile.PhoneNumber, Convert.ToBase64String(profile.Version));
}
