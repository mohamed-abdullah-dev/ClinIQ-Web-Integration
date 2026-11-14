using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Clinics;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Invitations;

internal sealed class WorkInvitationService(AppDbContext db, WorkforceAccess access, UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles, WorkMessageDelivery delivery, TimeProvider clock) : IWorkInvitationService
{
    public async Task<Result<InvitationDto>> IssueAsync(WorkInvitationKind kind, Guid? branch, Guid? location, string email, string terms, decimal? fee, string? currency, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationDto>.Failure(actor.Errors);
        if (!await access.CanIssueAsync(actor.Value.Id, kind, branch, location, ct)) return InvitationErrors.NotFound;
        ConsultationPrice? price = null;
        if (kind == WorkInvitationKind.Doctor)
        {
            if (!fee.HasValue) return InvitationErrors.InvalidDetails;
            var valid = ConsultationPrice.Create(fee.Value, currency); if (valid.IsError) return Result<InvitationDto>.Failure(valid.Errors); price = valid.Value;
        }
        else if (fee.HasValue || currency is not null) return InvitationErrors.InvalidDetails;
        var normalized = users.NormalizeEmail(email.Trim()); var secret = SessionSecrets.Create();
        var target = await db.Users.AsNoTracking().Where(x => x.NormalizedEmail == normalized).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        WorkInvitation invitation;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await access.LockAsync(WorkforceAccess.ScopeLock(branch, location), ct)) return InvitationErrors.Busy;
            if (!await access.CanIssueAsync(actor.Value.Id, kind, branch, location, ct)) return InvitationErrors.NotFound;
            var prior = await db.WorkInvitations.Where(x => x.Kind == kind && x.BranchId == branch && x.LocationId == location && x.NormalizedEmail == normalized && x.Status == WorkInvitationStatus.Pending).SingleOrDefaultAsync(ct);
            if (prior is not null)
            {
                if (clock.GetUtcNow() < prior.ExpiresAtUtc) return InvitationErrors.PendingExists;
                prior.Close(actor.Value.Id, WorkInvitationStatus.Expired, "Expired before replacement", clock.GetUtcNow()); await db.SaveChangesAsync(ct);
            }
            if (target.HasValue && await AssignedAsync(kind, branch, location, target.Value, ct)) return InvitationErrors.AlreadyAssigned;
            var row = WorkInvitation.Create(Guid.NewGuid(), kind, branch, location, actor.Value.Id, email, normalized!, target, SessionSecrets.Hash(secret), terms, price, clock.GetUtcNow());
            if (row.IsError) return Result<InvitationDto>.Failure(row.Errors);
            invitation = row.Value; db.WorkInvitations.Add(invitation); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return await DeliverAsync(invitation, secret, ct);
    }
    public async Task<Result<InvitationDto>> ResendAsync(Guid id, string version, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationDto>.Failure(actor.Errors);
        var snapshot = await db.WorkInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (snapshot is null || !await access.CanIssueAsync(actor.Value.Id, snapshot.Kind, snapshot.BranchId, snapshot.LocationId, ct)) return InvitationErrors.NotFound;
        var secret = SessionSecrets.Create(); WorkInvitation next;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.BranchId, snapshot.LocationId), ct)) return InvitationErrors.Busy;
            var old = await db.WorkInvitations.SingleAsync(x => x.Id == id, ct);
            if (!WorkforceAccess.Matches(old.Version, version)) return InvitationErrors.Stale;
            if (!await access.CanIssueAsync(actor.Value.Id, old.Kind, old.BranchId, old.LocationId, ct)) return InvitationErrors.NotFound;
            if (clock.GetUtcNow() < old.CreatedAtUtc.AddMinutes(1)) return InvitationErrors.Cooldown;
            var closed = old.Close(actor.Value.Id, WorkInvitationStatus.Superseded, "Replaced by a resent invitation", clock.GetUtcNow());
            if (closed.IsError) return Result<InvitationDto>.Failure(closed.Errors);
            await db.SaveChangesAsync(ct);
            next = WorkInvitation.Create(Guid.NewGuid(), old.Kind, old.BranchId, old.LocationId, actor.Value.Id, old.Email, old.NormalizedEmail,
                old.TargetUserId, SessionSecrets.Hash(secret), old.Terms, old.ConsultationFee.HasValue ? ConsultationPrice.Create(old.ConsultationFee.Value, old.Currency).Value : null, clock.GetUtcNow(), old.Id).Value;
            db.WorkInvitations.Add(next); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return await DeliverAsync(next, secret, ct);
    }
    public async Task<Result<InvitationDto>> RevokeAsync(Guid id, string version, string reason, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationDto>.Failure(actor.Errors);
        var snapshot = await db.WorkInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (snapshot is null || !await access.CanIssueAsync(actor.Value.Id, snapshot.Kind, snapshot.BranchId, snapshot.LocationId, ct)) return InvitationErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.BranchId, snapshot.LocationId), ct)) return InvitationErrors.Busy;
        var row = await db.WorkInvitations.SingleAsync(x => x.Id == id, ct);
        if (!WorkforceAccess.Matches(row.Version, version)) return InvitationErrors.Stale;
        if (!await access.CanIssueAsync(actor.Value.Id, row.Kind, row.BranchId, row.LocationId, ct)) return InvitationErrors.NotFound;
        var changed = row.Close(actor.Value.Id, WorkInvitationStatus.Revoked, reason, clock.GetUtcNow()); if (changed.IsError) return Result<InvitationDto>.Failure(changed.Errors);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await DtoAsync(row, ct);
    }
    public async Task<Result<InvitationDto>> RespondAsync(Guid id, string secret, string version, bool accept, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationDto>.Failure(actor.Errors);
        var snapshot = await db.WorkInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.NormalizedEmail == actor.Value.NormalizedEmail && (x.TargetUserId == null || x.TargetUserId == actor.Value.Id), ct);
        if (snapshot is null || !InvitationValidation.Secret(secret) || !SessionSecrets.Matches(secret, snapshot.SecretHash)) return InvitationErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.BranchId, snapshot.LocationId), ct)) return InvitationErrors.Busy;
        var row = await db.WorkInvitations.SingleAsync(x => x.Id == id, ct);
        if (!WorkforceAccess.Matches(row.Version, version)) return InvitationErrors.Stale;
        // The issuer's live permission is necessary at acceptance, including invitations issued before account creation.
        if (row.Status != WorkInvitationStatus.Pending) return InvitationErrors.Closed;
        if (clock.GetUtcNow() >= row.ExpiresAtUtc) return InvitationErrors.Expired;
        if (!await access.CanIssueAsync(row.IssuerUserId, row.Kind, row.BranchId, row.LocationId, ct)) return InvitationErrors.Forbidden;
        var rechecked = await access.ActorAsync(ct); if (rechecked.IsError) return Result<InvitationDto>.Failure(rechecked.Errors);
        var profile = row.Kind == WorkInvitationKind.Doctor ? await access.Doctors.SingleOrDefaultAsync(x => x.UserId == actor.Value.Id, ct) : null;
        if (accept && row.Kind == WorkInvitationKind.Doctor && profile is null) return InvitationErrors.DoctorRequired;
        if (accept && row.Kind == WorkInvitationKind.Doctor && !await db.ClinicBranches.AnyAsync(b => b.Id == row.BranchId && b.IsActive && b.LifecycleState == Domain.Clinics.BranchLifecycleState.Open, ct))
            return InvitationErrors.Forbidden;
        if (accept && await AssignedAsync(row.Kind, row.BranchId, row.LocationId, actor.Value.Id, ct)) return InvitationErrors.AlreadyAssigned;
        // Provision the role before mutating the invitation; Identity saves its shared DbContext.
        if (accept && row.Kind == WorkInvitationKind.Receptionist)
        {
            if (!await new ClinicRoleProvisioner(db, roles).EnsureAsync("Receptionist", ct)) return InvitationErrors.Busy;
            var user = await users.FindByIdAsync(actor.Value.Id.ToString()) ?? throw new InvalidOperationException("Recipient account is missing.");
            if (!await users.IsInRoleAsync(user, "Receptionist") && !(await users.AddToRoleAsync(user, "Receptionist")).Succeeded) throw new InvalidOperationException("Receptionist role assignment failed.");
        }
        var changed = row.Respond(actor.Value.Id, rechecked.Value.NormalizedEmail!, accept, clock.GetUtcNow()); if (changed.IsError) return Result<InvitationDto>.Failure(changed.Errors);
        if (accept)
        {
            if (row.Kind == WorkInvitationKind.Doctor)
            {
                var branch = await db.ClinicBranches.SingleAsync(x => x.Id == row.BranchId, ct);
                var place = DoctorPracticeLocation.FromInvitation(row, profile!, branch);
                if (place.IsError) return Result<InvitationDto>.Failure(place.Errors); db.DoctorPracticeLocations.Add(place.Value);
            }
            else db.ReceptionistAssignments.Add(ReceptionistAssignment.FromAccepted(row).Value);
        }
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await DtoAsync(row, ct); }
        catch (DbUpdateConcurrencyException) { return InvitationErrors.Stale; }
    }
    private Task<bool> AssignedAsync(WorkInvitationKind kind, Guid? branch, Guid? location, Guid user, CancellationToken ct) => kind == WorkInvitationKind.Doctor
        ? db.DoctorPracticeLocations.AnyAsync(x => x.UserId == user && x.ClinicBranchId == branch, ct)
        : db.ReceptionistAssignments.AnyAsync(x => x.UserId == user && x.BranchId == branch && x.LocationId == location && x.IsActive, ct);
    private IQueryable<WorkInvitation> Visible(AppUser actor, bool received) => received
        ? db.WorkInvitations.AsNoTracking().Where(x => x.NormalizedEmail == actor.NormalizedEmail && (x.TargetUserId == null || x.TargetUserId == actor.Id))
        : db.WorkInvitations.AsNoTracking().Where(x => x.IssuerUserId == actor.Id ||
            (x.BranchId != null && db.ClinicBranches.Any(b => b.Id == x.BranchId && b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive) &&
                db.ClinicManagementMemberships.Any(m => m.ClinicId == b.ClinicId && m.UserId == actor.Id && m.IsActive)) &&
                db.UserRoles.Any(ur => ur.UserId == actor.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "CLINICMANAGER"))) ||
            (x.LocationId != null && db.DoctorPracticeLocations.Any(p => p.Id == x.LocationId && p.ClinicBranchId == null && p.UserId == actor.Id && access.Doctors.Any(d => d.Id == p.DoctorProfileId))));
    public async Task<Result<InvitationPage>> ListAsync(bool received, int page, int pageSize, CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationPage>.Failure(actor.Errors);
        var query = Visible(actor.Value, received); var count = await query.CountAsync(ct);
        var now = clock.GetUtcNow();
        var dtos = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize)
            .Select(row => new InvitationDto(row.Id, row.Kind, row.BranchId, row.LocationId,
                row.BranchId != null ? db.ClinicBranches.Where(b => b.Id == row.BranchId).Select(b => b.Name).Single() : db.DoctorPracticeLocations.Where(p => p.Id == row.LocationId).Select(p => p.Name).Single(),
                row.Email, row.Terms, row.ConsultationFee, row.Currency, row.Status == WorkInvitationStatus.Pending && now >= row.ExpiresAtUtc ? "Expired" : row.Status.ToString(),
                row.ExpiresAtUtc, row.DeliveryStatus, Convert.ToBase64String(row.Version), row.ReplacesInvitationId)).ToArrayAsync(ct);
        return new InvitationPage(dtos, count, page, pageSize);
    }
    public async Task<Result<InvitationDto>> GetAsync(Guid id, bool received, CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<InvitationDto>.Failure(actor.Errors);
        var row = await Visible(actor.Value, received).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? InvitationErrors.NotFound : await DtoAsync(row, ct);
    }
    private async Task<InvitationDto> DeliverAsync(WorkInvitation row, string secret, CancellationToken ct)
    {
        var state = await delivery.InvitationAsync(row.Id, row.Email, secret, row.Terms, row.ConsultationFee, row.Currency, row.ExpiresAtUtc, ct);
        // Cancellation after commit must not undo the invitation or leave delivery status falsely successful.
        await db.WorkInvitations.Where(x => x.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryStatus, state), CancellationToken.None);
        await db.Entry(row).ReloadAsync(CancellationToken.None); return await DtoAsync(row, CancellationToken.None);
    }
    private async Task<InvitationDto> DtoAsync(WorkInvitation row, CancellationToken ct)
    {
        var place = row.BranchId is Guid branch ? await db.ClinicBranches.Where(x => x.Id == branch).Select(x => x.Name).SingleAsync(ct)
            : await db.DoctorPracticeLocations.Where(x => x.Id == row.LocationId).Select(x => x.Name).SingleAsync(ct);
        var status = row.Status == WorkInvitationStatus.Pending && clock.GetUtcNow() >= row.ExpiresAtUtc ? "Expired" : row.Status.ToString();
        return new InvitationDto(row.Id, row.Kind, row.BranchId, row.LocationId, place, row.Email, row.Terms, row.ConsultationFee, row.Currency, status, row.ExpiresAtUtc, row.DeliveryStatus, Convert.ToBase64String(row.Version), row.ReplacesInvitationId);
    }
}
