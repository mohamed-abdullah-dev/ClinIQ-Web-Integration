using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Invitations;

internal sealed class WorkforceAccess(AppDbContext db, ICurrentUser current)
{
    public async Task<Result<AppUser>> ActorAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return InvitationErrors.Unauthorized;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EmailConfirmed, ct);
        return user is null ? InvitationErrors.Forbidden : user;
    }
    public IQueryable<DoctorProfile> Doctors => db.DoctorProfiles.Where(p => db.Users.Any(u => u.Id == p.UserId && u.EmailConfirmed) &&
        db.UserRoles.Any(ur => ur.UserId == p.UserId && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "DOCTOR")) &&
        db.DoctorRegistrations.Any(r => r.Id == p.DoctorRegistrationId && r.UserId == p.UserId && r.IsCurrent && r.Status == DoctorRegistrationStatus.Approved));
    public Task<bool> ManagesBranchAsync(Guid actor, Guid branch, CancellationToken ct) => db.ClinicBranches.AnyAsync(b => b.Id == branch && b.IsActive &&
        db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive) && db.ClinicManagementMemberships.Any(m => m.ClinicId == b.ClinicId && m.UserId == actor && m.IsActive) &&
        db.Users.Any(u => u.Id == actor && u.EmailConfirmed) && db.UserRoles.Any(ur => ur.UserId == actor && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "CLINICMANAGER")), ct);
    public Task<bool> OwnsIndependentAsync(Guid actor, Guid location, CancellationToken ct) => db.DoctorPracticeLocations.AnyAsync(p =>
        p.Id == location && p.UserId == actor && p.ClinicBranchId == null && p.IsActive && p.Status == PracticeLocationStatus.Approved && Doctors.Any(d => d.Id == p.DoctorProfileId), ct);
    public async Task<bool> CanIssueAsync(Guid actor, WorkInvitationKind kind, Guid? branch, Guid? location, CancellationToken ct) =>
        branch.HasValue ? location is null && await ManagesBranchAsync(actor, branch.Value, ct) :
        kind == WorkInvitationKind.Receptionist && location.HasValue && await OwnsIndependentAsync(actor, location.Value, ct);
    // Revoking an existing standalone assignment stays with its doctor even after linking or departure.
    public Task<bool> CanRevokeAssignmentAsync(Guid actor, Guid? branch, Guid? location, CancellationToken ct) => branch.HasValue
        ? ManagesBranchAsync(actor, branch.Value, ct)
        : db.DoctorPracticeLocations.AnyAsync(p => p.Id == location && p.UserId == actor && Doctors.Any(d => d.Id == p.DoctorProfileId), ct);
    public void Fresh() { if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Workforce operations require a context without pending writes."); }
    public async Task<bool> LockAsync(string resource, CancellationToken ct)
    {
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (codes.Single() < -3) throw new InvalidOperationException("Unexpected workforce lock failure."); return codes.Single() >= 0;
    }
    public static string ScopeLock(Guid? branch, Guid? location) => $"ClinIQ.Workforce.{(branch ?? location!.Value):N}";
    public static bool Matches(byte[] version, string candidate) => Convert.ToBase64String(version) == candidate;
}
