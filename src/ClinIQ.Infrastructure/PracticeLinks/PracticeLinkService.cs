using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.PracticeLinks;
using ClinIQ.Application.Features.PracticeLinks.Commands;
using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLinks;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.PracticeLinks;

internal sealed class PracticeLinkService(AppDbContext db, ICurrentUser current, IHttpContextAccessor http,
    WorkforceAccess access, TimeProvider clock) : IPracticeLinkService
{
    private async Task<Result<Guid>> Actor(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return PracticeLinkErrors.Unauthorized;
        if (http.HttpContext?.User.FindFirst("client")?.Value != "Web" || !await db.Users.AnyAsync(u => u.Id == id && u.EmailConfirmed &&
            db.UserRoles.Any(ur => ur.UserId == id && db.Roles.Any(r => r.Id == ur.RoleId && (r.NormalizedName == "DOCTOR" || r.NormalizedName == "CLINICMANAGER"))), ct))
            return PracticeLinkErrors.Forbidden;
        return id;
    }
    private IQueryable<PracticeLinkRequest> Visible(Guid actor) => db.PracticeLinkRequests.AsNoTracking().Where(r =>
        (r.DoctorUserId == actor && access.Doctors.Any(d => d.UserId == actor)) ||
        db.ClinicBranches.Any(b => b.Id == r.BranchId && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive) &&
            db.ClinicManagementMemberships.Any(m => m.ClinicId == b.ClinicId && m.UserId == actor && m.IsActive) &&
            db.UserRoles.Any(ur => ur.UserId == actor && db.Roles.Any(role => role.Id == ur.RoleId && role.NormalizedName == "CLINICMANAGER"))));
    private Task<bool> EligibleDoctor(DoctorPracticeLocation p, CancellationToken ct) => access.Doctors.AnyAsync(d => d.Id == p.DoctorProfileId && d.UserId == p.UserId, ct);
    private async Task<Error?> Sources(DoctorPracticeLocation p, ClinicBranch b, CancellationToken ct)
    {
        if (!PracticeLinkRequest.Eligible(p, b) || !await EligibleDoctor(p, ct) || !await db.Clinics.AnyAsync(c => c.Id == b.ClinicId && c.IsActive, ct)) return PracticeLinkErrors.Unavailable;
        if (await db.DoctorPracticeLocations.AnyAsync(x => x.DoctorProfileId == p.DoctorProfileId && x.ClinicBranchId == b.Id, ct)) return PracticeLinkErrors.Duplicate;
        return null;
    }
    private static LinkSourceDto Source(DoctorPracticeLocation p, ClinicBranch b) => new(p.Id, b.Id, p.Name, p.Address, b.Name, b.Address,
        p.ConsultationFee, p.Currency, Convert.ToBase64String(p.Version), Convert.ToBase64String(b.Version), PracticeLinkRequest.CurrentTerms);
    private static LinkRequestDto Dto(PracticeLinkRequest r) => new(r.Id, r.DoctorUserId,
        new(r.LocationId, r.BranchId, r.LocationName, r.LocationAddress, r.BranchName, r.BranchAddress, r.ConsultationFee, r.Currency,
            Convert.ToBase64String(r.LocationVersion), Convert.ToBase64String(r.BranchVersion), r.Terms), r.Status, r.RequestedAtUtc,
        r.DecidedByUserId, r.DecidedAtUtc, r.DecisionReason, Convert.ToBase64String(r.Version));
    public async Task<Result<LinkSourceDto>> PreviewAsync(Guid location, Guid branch, CancellationToken ct)
    {
        var actor = await Actor(ct); if (actor.IsError) return Result<LinkSourceDto>.Failure(actor.Errors);
        var p = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == location && x.UserId == actor.Value, ct);
        if (p is null || !await EligibleDoctor(p, ct)) return PracticeLinkErrors.NotFound;
        var b = await db.ClinicBranches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == branch && x.IsActive, ct);
        if (b is null) return PracticeLinkErrors.NotFound;
        var error = await Sources(p, b, ct); return error is not null ? error : Source(p, b);
    }
    private async Task<bool> LockSources(DoctorPracticeLocation p, Guid branch, CancellationToken ct)
    {
        // Serialize publication, booking and lifecycle first, then invitation/assignment writes, then premises/fee edits.
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{p.DoctorProfileId:N}", ct)) return false;
        foreach (var id in new[] { p.Id, branch }.Order()) if (!await access.LockAsync(WorkforceAccess.ScopeLock(null, id), ct)) return false;
        if (!await access.LockAsync($"ClinIQ.PracticeLocation.{p.Id:N}", ct) || !await access.LockAsync($"ClinIQ.ClinicBranch.{branch:N}", ct)) return false;
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorPracticeLocations WITH(UPDLOCK,HOLDLOCK) WHERE Id={p.Id}").ToArrayAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM ClinicBranches WITH(UPDLOCK,HOLDLOCK) WHERE Id={branch}").ToArrayAsync(ct);
        return true;
    }
    private async Task HoldActor(Guid id, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH(HOLDLOCK) WHERE Id={id}").ToArrayAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH(HOLDLOCK) WHERE UserId={id}").ToArrayAsync(ct);
    }
    public async Task<Result<LinkRequestDto>> CreateAsync(LinkCreateInput input, CancellationToken ct)
    {
        access.Fresh(); if (input is null || !(await new LinkCreateValidator().ValidateAsync(input, ct)).IsValid) return PracticeLinkErrors.Input;
        var actor = await Actor(ct); if (actor.IsError) return Result<LinkRequestDto>.Failure(actor.Errors);
        var snapshot = await db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(p => p.Id == input.LocationId && p.UserId == actor.Value, ct);
        if (snapshot is null || !await EligibleDoctor(snapshot, ct)) return PracticeLinkErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockSources(snapshot, input.BranchId, ct)) return PracticeLinkErrors.Busy;
        await HoldActor(actor.Value, ct); var live = await Actor(ct); if (live.IsError) return Result<LinkRequestDto>.Failure(live.Errors);
        var p = await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(x => x.Id == input.LocationId, ct);
        var b = await db.ClinicBranches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.BranchId, ct); if (b is null) return PracticeLinkErrors.NotFound;
        var error = await Sources(p, b, ct); if (error is not null) return error;
        if (!WorkforceAccess.Matches(p.Version, input.LocationVersion) || !WorkforceAccess.Matches(b.Version, input.BranchVersion)) return PracticeLinkErrors.Stale;
        var pending = await db.PracticeLinkRequests.AsNoTracking().SingleOrDefaultAsync(r => r.LocationId == p.Id && r.Status == PracticeLinkStatus.Pending, ct);
        if (pending is not null) return pending.BranchId == b.Id && pending.LocationVersion.SequenceEqual(p.Version) && pending.BranchVersion.SequenceEqual(b.Version)
            ? Dto(pending) : PracticeLinkErrors.Pending;
        var created = PracticeLinkRequest.Create(p, b, actor.Value, input.SamePremises, input.AcceptTerms, clock.GetUtcNow());
        if (created.IsError) return Result<LinkRequestDto>.Failure(created.Errors);
        db.PracticeLinkRequests.Add(created.Value); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(created.Value);
    }
    public async Task<Result<LinkRequestDto>> DecideAsync(Guid id, LinkDecisionInput input, CancellationToken ct)
    {
        if (input is null || !(await new LinkDecisionValidator().ValidateAsync(input, ct)).IsValid) return PracticeLinkErrors.Input;
        return await Close(id, input.Version, input.Accept == true ? PracticeLinkStatus.Accepted : PracticeLinkStatus.Rejected,
            input.Reason, input.SamePremises, input.AcceptTerms, ct);
    }
    public async Task<Result<LinkRequestDto>> WithdrawAsync(Guid id, LinkWithdrawInput input, CancellationToken ct)
    {
        if (input is null || !(await new LinkWithdrawValidator().ValidateAsync(input, ct)).IsValid) return PracticeLinkErrors.Input;
        return await Close(id, input.Version, PracticeLinkStatus.Withdrawn, input.Reason, false, false, ct);
    }
    private async Task<Result<LinkRequestDto>> Close(Guid id, string version, PracticeLinkStatus outcome, string reason, bool same, bool terms, CancellationToken ct)
    {
        access.Fresh(); var actor = await Actor(ct); if (actor.IsError) return Result<LinkRequestDto>.Failure(actor.Errors);
        var snapshot = await Visible(actor.Value).SingleOrDefaultAsync(r => r.Id == id, ct); if (snapshot is null) return PracticeLinkErrors.NotFound;
        var p0 = await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == snapshot.LocationId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockSources(p0, snapshot.BranchId, ct)) return PracticeLinkErrors.Busy;
        await HoldActor(actor.Value, ct); await HoldActor(p0.UserId, ct);
        var live = await Actor(ct); if (live.IsError) return Result<LinkRequestDto>.Failure(live.Errors);
        if (!await Visible(actor.Value).AnyAsync(r => r.Id == id, ct)) return PracticeLinkErrors.NotFound;
        if (outcome == PracticeLinkStatus.Withdrawn ? actor.Value != snapshot.DoctorUserId : !await access.ManagesBranchAsync(actor.Value, snapshot.BranchId, ct)) return PracticeLinkErrors.NotFound;
        var row = await db.PracticeLinkRequests.SingleAsync(r => r.Id == id, ct);
        if (row.Status != PracticeLinkStatus.Pending)
            return row.Status == outcome && row.DecidedByUserId == actor.Value && row.DecisionReason == reason.Trim() ? Dto(row) : PracticeLinkErrors.Closed;
        if (!WorkforceAccess.Matches(row.Version, version)) return PracticeLinkErrors.Stale;
        var p = await db.DoctorPracticeLocations.SingleAsync(x => x.Id == row.LocationId, ct);
        var b = await db.ClinicBranches.AsNoTracking().SingleAsync(x => x.Id == row.BranchId, ct);
        if (outcome == PracticeLinkStatus.Accepted)
        {
            // Protect the live membership/clinic/doctor decision through commit, not just at authentication.
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM ClinicManagementMemberships WITH(HOLDLOCK) WHERE ClinicId={b.ClinicId} AND UserId={actor.Value}").ToArrayAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM Clinics WITH(HOLDLOCK) WHERE Id={b.ClinicId}").ToArrayAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorRegistrations WITH(HOLDLOCK) WHERE UserId={p.UserId}").ToArrayAsync(ct);
            if (!await access.ManagesBranchAsync(actor.Value, b.Id, ct)) return PracticeLinkErrors.NotFound;
            var error = await Sources(p, b, ct); if (error is not null) return error;
            if (!row.LocationVersion.SequenceEqual(p.Version) || !row.BranchVersion.SequenceEqual(b.Version)) return PracticeLinkErrors.Stale;
        }
        var decision = row.Decide(actor.Value, outcome, reason, same, terms, clock.GetUtcNow());
        if (decision.IsError) return Result<LinkRequestDto>.Failure(decision.Errors);
        if (outcome == PracticeLinkStatus.Accepted)
        {
            var linked = p.LinkToBranch(row, b); if (linked.IsError) return Result<LinkRequestDto>.Failure(linked.Errors);
        }
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row); }
        catch (DbUpdateConcurrencyException) { return PracticeLinkErrors.Stale; }
    }
    public async Task<Result<LinkRequestDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = await Actor(ct); if (actor.IsError) return Result<LinkRequestDto>.Failure(actor.Errors);
        var row = await Visible(actor.Value).SingleOrDefaultAsync(r => r.Id == id, ct); return row is null ? PracticeLinkErrors.NotFound : Dto(row);
    }
    public async Task<Result<LinkPage>> ListAsync(PracticeLinkStatus? status, int page, int size, CancellationToken ct)
    {
        if (!LifecycleValidation.Page(page, size) || status.HasValue && !Enum.IsDefined(status.Value)) return PracticeLinkErrors.Input;
        var actor = await Actor(ct); if (actor.IsError) return Result<LinkPage>.Failure(actor.Errors);
        var query = Visible(actor.Value); if (status.HasValue) query = query.Where(r => r.Status == status);
        var count = await query.CountAsync(ct); var rows = await query.OrderByDescending(r => r.RequestedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new LinkPage(rows.Select(Dto).ToArray(), count, page, size);
    }
}
