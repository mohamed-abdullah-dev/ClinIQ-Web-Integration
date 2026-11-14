using ClinIQ.Application.Features.Invitations;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Invitations;

internal sealed class WorkforceService(AppDbContext db, WorkforceAccess access, WorkMessageDelivery delivery, TimeProvider clock) : IWorkforceService
{
    public async Task<Result<IReadOnlyList<AssignmentDto>>> ListAssignmentsAsync(CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<AssignmentDto>>.Failure(actor.Errors);
        var rows = await db.ReceptionistAssignments.AsNoTracking().Where(a => a.UserId == actor.Value.Id ||
            (a.BranchId != null && db.UserRoles.Any(ur => ur.UserId == actor.Value.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "CLINICMANAGER")) &&
                db.ClinicBranches.Any(b => b.Id == a.BranchId && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive) && db.ClinicManagementMemberships.Any(m => m.ClinicId == b.ClinicId && m.UserId == actor.Value.Id && m.IsActive))) ||
            (a.LocationId != null && db.DoctorPracticeLocations.Any(p => p.Id == a.LocationId && p.UserId == actor.Value.Id && access.Doctors.Any(d => d.Id == p.DoctorProfileId))))
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToArrayAsync(ct);
        return Result<IReadOnlyList<AssignmentDto>>.Success(rows.Select(Dto).ToArray());
    }
    public async Task<Result<AssignmentDto>> RevokeAssignmentAsync(Guid id, string version, string reason, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<AssignmentDto>.Failure(actor.Errors);
        var snapshot = await db.ReceptionistAssignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (snapshot is null || !await access.CanRevokeAssignmentAsync(actor.Value.Id, snapshot.BranchId, snapshot.LocationId, ct)) return InvitationErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.BranchId, snapshot.LocationId), ct)) return InvitationErrors.Busy;
        var row = await db.ReceptionistAssignments.SingleAsync(x => x.Id == id, ct);
        if (!WorkforceAccess.Matches(row.Version, version)) return InvitationErrors.Stale;
        if (!await access.CanRevokeAssignmentAsync(actor.Value.Id, row.BranchId, row.LocationId, ct)) return InvitationErrors.NotFound;
        var changed = row.Revoke(actor.Value.Id, reason, clock.GetUtcNow()); if (changed.IsError) return Result<AssignmentDto>.Failure(changed.Errors);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row); } catch (DbUpdateConcurrencyException) { return InvitationErrors.Stale; }
    }
    // Discovery only. Attendance additionally checks each session's retained OperationalBranchId.
    private IQueryable<DoctorPracticeLocation> ReceptionPlaces(Guid actor) => db.DoctorPracticeLocations.AsNoTracking().Where(p => p.IsActive && p.Status == PracticeLocationStatus.Approved && access.Doctors.Any(d => d.Id == p.DoctorProfileId) &&
        db.UserRoles.Any(ur => ur.UserId == actor && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "RECEPTIONIST")) &&
        ((db.ReceptionistAssignments.Any(a => a.UserId == actor && a.IsActive && a.LocationId == p.Id && a.BranchId == null)) ||
         (p.ClinicBranchId != null && db.ClinicBranches.Any(b => b.Id == p.ClinicBranchId && b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive)) &&
          db.ReceptionistAssignments.Any(a => a.UserId == actor && a.IsActive && a.BranchId == p.ClinicBranchId && a.LocationId == null))));
    public async Task<Result<IReadOnlyList<AuthorizedDoctorDto>>> AuthorizedDoctorsAsync(CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<AuthorizedDoctorDto>>.Failure(actor.Errors);
        var visible = ReceptionPlaces(actor.Value.Id);
        return Result<IReadOnlyList<AuthorizedDoctorDto>>.Success(await access.Doctors.AsNoTracking().Where(d => visible.Any(p => p.DoctorProfileId == d.Id))
            .OrderBy(d => d.ProfessionalName).ThenBy(d => d.Id).Select(d => new AuthorizedDoctorDto(d.Id, d.ProfessionalName)).ToArrayAsync(ct));
    }
    public async Task<Result<IReadOnlyList<AuthorizedPlaceDto>>> AuthorizedPlacesAsync(Guid doctorId, CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<AuthorizedPlaceDto>>.Failure(actor.Errors);
        var query = ReceptionPlaces(actor.Value.Id).Where(p => p.DoctorProfileId == doctorId);
        if (!await query.AnyAsync(ct)) return InvitationErrors.NotFound;
        var independent = await query.Where(p => db.ReceptionistAssignments.Any(a => a.UserId == actor.Value.Id && a.IsActive && a.LocationId == p.Id && a.BranchId == null))
            .Select(p => new AuthorizedPlaceDto(p.Id, p.DoctorProfileId, null, p.Name, p.Address, p.ConsultationFee, p.Currency, Convert.ToBase64String(p.Version))).ToArrayAsync(ct);
        var managed = await query.Where(p => p.ClinicBranchId != null && db.ClinicBranches.Any(b => b.Id == p.ClinicBranchId && b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive)) &&
            db.ReceptionistAssignments.Any(a => a.UserId == actor.Value.Id && a.IsActive && a.BranchId == p.ClinicBranchId && a.LocationId == null))
            .Select(p => new AuthorizedPlaceDto(p.Id, p.DoctorProfileId, p.ClinicBranchId,
                db.ClinicBranches.Where(b => b.Id == p.ClinicBranchId).Select(b => b.Name).Single(),
                db.ClinicBranches.Where(b => b.Id == p.ClinicBranchId).Select(b => b.Address).Single(), p.ConsultationFee, p.Currency, Convert.ToBase64String(p.Version))).ToArrayAsync(ct);
        return Result<IReadOnlyList<AuthorizedPlaceDto>>.Success(independent.Concat(managed).OrderBy(p => p.LocationId).ThenBy(p => p.BranchId).ToArray());
    }
    public async Task<Result<FeeChangeDto>> ChangeFeeAsync(Guid locationId, decimal amount, string currency, string version, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<FeeChangeDto>.Failure(actor.Errors);
        var price = ConsultationPrice.Create(amount, currency); if (price.IsError) return Result<FeeChangeDto>.Failure(price.Errors);
        PracticeFeeChange notice;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await access.LockAsync($"ClinIQ.PracticeLocation.{locationId:N}", ct)) return InvitationErrors.Busy;
            var place = await db.DoctorPracticeLocations.SingleOrDefaultAsync(p => p.Id == locationId, ct);
            if (place?.ClinicBranchId is not Guid branch || !await access.ManagesBranchAsync(actor.Value.Id, branch, ct)) return InvitationErrors.NotFound;
            if (!WorkforceAccess.Matches(place.Version, version)) return InvitationErrors.Stale;
            if (place.ConsultationFee == price.Value.Amount) return InvitationErrors.InvalidDetails;
            var previous = place.ConsultationFee; var changed = place.ChangeManagedFee(price.Value); if (changed.IsError) return Result<FeeChangeDto>.Failure(changed.Errors);
            notice = PracticeFeeChange.Create(place.Id, place.UserId, actor.Value.Id, previous, place.ConsultationFee); db.PracticeFeeChanges.Add(notice);
            try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); } catch (DbUpdateConcurrencyException) { return InvitationErrors.Stale; }
        }
        return await DeliverFeeAsync(notice, ct);
    }
    public async Task<Result<IReadOnlyList<FeeChangeDto>>> FeeHistoryAsync(Guid locationId, CancellationToken ct)
    {
        var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<FeeChangeDto>>.Failure(actor.Errors);
        var place = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(p => p.Id == locationId, ct);
        if (place?.ClinicBranchId is not Guid branch || (place.UserId != actor.Value.Id && !await access.ManagesBranchAsync(actor.Value.Id, branch, ct))) return InvitationErrors.NotFound;
        var rows = await db.PracticeFeeChanges.AsNoTracking().Where(x => x.LocationId == locationId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        return Result<IReadOnlyList<FeeChangeDto>>.Success(rows.Select(Dto).ToArray());
    }
    public async Task<Result<FeeChangeDto>> RetryFeeNoticeAsync(Guid id, CancellationToken ct)
    {
        access.Fresh(); var actor = await access.ActorAsync(ct); if (actor.IsError) return Result<FeeChangeDto>.Failure(actor.Errors);
        var row = await db.PracticeFeeChanges.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return InvitationErrors.NotFound;
        var branch = await db.DoctorPracticeLocations.Where(p => p.Id == row.LocationId).Select(p => p.ClinicBranchId).SingleAsync(ct);
        if (!branch.HasValue || !await access.ManagesBranchAsync(actor.Value.Id, branch.Value, ct)) return InvitationErrors.NotFound;
        return await DeliverFeeAsync(row, ct);
    }
    private async Task<Result<FeeChangeDto>> DeliverFeeAsync(PracticeFeeChange row, CancellationToken ct)
    {
        await db.Entry(row).ReloadAsync(ct);
        if (row.DeliveryStatus == "DevelopmentFile") return Dto(row);
        var attempt = clock.GetUtcNow(); var cutoff = attempt.AddMinutes(-1);
        var claimed = await db.PracticeFeeChanges.Where(x => x.Id == row.Id && x.DeliveryStatus != "DevelopmentFile" &&
            (x.LastDeliveryAttemptUtc == null || x.LastDeliveryAttemptUtc <= cutoff)).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastDeliveryAttemptUtc, attempt), ct);
        if (claimed == 0) return InvitationErrors.Cooldown;
        var email = await db.Users.AsNoTracking().Where(u => u.Id == row.DoctorUserId && u.EmailConfirmed).Select(u => u.Email).SingleOrDefaultAsync(ct);
        var status = email is null ? "Unavailable" : await delivery.FeeAsync(row.Id, email, row.LocationId, row.PreviousFee, row.NewFee, ct);
        var now = clock.GetUtcNow();
        await db.PracticeFeeChanges.Where(x => x.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryStatus, status).SetProperty(x => x.LastDeliveryAttemptUtc, now), CancellationToken.None);
        await db.Entry(row).ReloadAsync(CancellationToken.None); return Dto(row);
    }
    private static AssignmentDto Dto(ReceptionistAssignment a) => new(a.Id, a.UserId, a.BranchId, a.LocationId, a.SourceInvitationId, a.IsActive, a.RevokedByUserId, a.RevokedAtUtc, a.RevocationReason, Convert.ToBase64String(a.Version));
    private static FeeChangeDto Dto(PracticeFeeChange x) => new(x.Id, x.LocationId, x.PreviousFee, x.NewFee, x.Currency, x.DeliveryStatus);
}
