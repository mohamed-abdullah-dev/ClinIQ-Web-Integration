using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.PracticeLifecycle;
using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLifecycle;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Appointments;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.PracticeLifecycle;

internal sealed class PracticeLifecycleService(AppDbContext db, ICurrentUser current, IHttpContextAccessor http,
    WorkforceAccess access, BookingNoticeDelivery notices, TimeProvider clock) : IPracticeLifecycleService
{
    private sealed record Scope(OperationalScopeKind Kind, Guid Id, DoctorPracticeLocation? Place, ClinicBranch? Branch, ClinicBranch? Parent)
    {
        public int State => Place is null ? (int)Branch!.LifecycleState : (int)Place.LifecycleState;
        public bool Active => Place?.IsActive ?? Branch!.IsActive;
        public byte[] Version => Place?.Version ?? Branch!.Version;
        public bool Closed => !Active || State == 3 || (Parent is not null && (!Parent.IsActive || Parent.LifecycleState == BranchLifecycleState.Closed));
        public bool Pending => State == 2 || Parent?.LifecycleState == BranchLifecycleState.PendingClosure;
    }
    private Task<bool> Role(Guid actor, string role, CancellationToken ct) => db.UserRoles.AnyAsync(ur => ur.UserId == actor && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == role), ct);
    private async Task<Result<Guid>> Actor(bool admin, CancellationToken ct)
    {
        if (current.UserId is not Guid actor) return LifecycleErrors.Unauthorized;
        if (http.HttpContext?.User.FindFirst("client")?.Value != "Web" || !await db.Users.AnyAsync(u => u.Id == actor && u.EmailConfirmed, ct)) return LifecycleErrors.Forbidden;
        if (admin ? !await Role(actor, "ADMIN", ct) : !(await Role(actor, "DOCTOR", ct) || await Role(actor, "CLINICMANAGER", ct))) return LifecycleErrors.Forbidden;
        return actor;
    }
    private async Task HoldActor(Guid actor, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH(HOLDLOCK) WHERE Id={actor}").ToArrayAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH(HOLDLOCK) WHERE UserId={actor}").ToArrayAsync(ct);
    }
    private async Task<Scope?> Load(OperationalScopeKind kind, Guid id, bool tracked, CancellationToken ct)
    {
        if (kind == OperationalScopeKind.Location)
        {
            var query = tracked ? db.DoctorPracticeLocations : db.DoctorPracticeLocations.AsNoTracking();
            var p = await query.SingleOrDefaultAsync(x => x.Id == id, ct); if (p is null) return null;
            var parent = p.ClinicBranchId.HasValue ? await db.ClinicBranches.AsNoTracking().SingleAsync(b => b.Id == p.ClinicBranchId, ct) : null;
            return new(kind, id, p, null, parent);
        }
        if (kind != OperationalScopeKind.Branch) return null;
        var branches = tracked ? db.ClinicBranches : db.ClinicBranches.AsNoTracking();
        var branch = await branches.SingleOrDefaultAsync(b => b.Id == id, ct);
        return branch is null ? null : new(kind, id, null, branch, null);
    }
    private async Task<bool> Manages(Guid actor, Guid branch, CancellationToken ct) => await Role(actor, "CLINICMANAGER", ct) &&
        await db.ClinicBranches.AnyAsync(b => b.Id == branch && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive) &&
            db.ClinicManagementMemberships.Any(m => m.ClinicId == b.ClinicId && m.UserId == actor && m.IsActive), ct);
    private async Task<bool> Authorized(Scope scope, Guid actor, CancellationToken ct) => scope.Place is { } p
        ? (p.UserId == actor && await access.Doctors.AnyAsync(d => d.Id == p.DoctorProfileId && d.UserId == actor, ct)) ||
            (p.ClinicBranchId.HasValue && await Manages(actor, p.ClinicBranchId.Value, ct))
        : await Manages(actor, scope.Id, ct);
    private IQueryable<BookingSession> Sessions(Scope scope) => scope.Kind == OperationalScopeKind.Location
        ? db.BookingSessions.Where(s => s.LocationId == scope.Id)
        : db.BookingSessions.Where(s => s.OperationalBranchId == scope.Id || db.DoctorPracticeLocations.Any(p => p.Id == s.LocationId && p.ClinicBranchId == scope.Id));
    private IQueryable<Appointment> Affected(Scope scope)
    {
        var sessions = Sessions(scope);
        return db.Appointments.Where(a => (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn) && sessions.Any(s => s.Id == a.SessionId));
    }
    private Task<Guid[]> Doctors(Scope scope, CancellationToken ct) => scope.Place is { } p
        ? Task.FromResult(new[] { p.DoctorProfileId })
        : db.DoctorPracticeLocations.Where(x => x.ClinicBranchId == scope.Id).Select(x => x.DoctorProfileId)
            .Union(Sessions(scope).Select(s => s.DoctorProfileId)).Distinct().OrderBy(x => x).ToArrayAsync(ct);
    private async Task<bool> LockScope(Scope snapshot, CancellationToken ct)
    {
        // All clinical writers take the doctor lock first. Do not invert this order with the workforce scope.
        var doctors = await Doctors(snapshot, ct);
        foreach (var id in doctors.Order()) if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{id:N}", ct)) return false;
        var branch = snapshot.Kind == OperationalScopeKind.Branch ? snapshot.Id : snapshot.Place!.ClinicBranchId;
        if (!await access.LockAsync(WorkforceAccess.ScopeLock(branch, branch is null ? snapshot.Id : null), ct)) return false;
        // An invitation may have added a doctor before we acquired the scope lock; retry without acquiring locks backwards.
        if ((await Doctors(snapshot, ct)).Except(doctors).Any()) return false;
        if (snapshot.Kind == OperationalScopeKind.Location)
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorPracticeLocations WITH(UPDLOCK,HOLDLOCK) WHERE Id={snapshot.Id}").ToArrayAsync(ct);
        else
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM ClinicBranches WITH(UPDLOCK,HOLDLOCK) WHERE Id={snapshot.Id}").ToArrayAsync(ct);
        return true;
    }
    private async Task<LifecycleScopeDto> Dto(Scope scope, CancellationToken ct)
    {
        var counts = await Affected(scope).GroupBy(a => a.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var confirmed = counts.SingleOrDefault(x => x.Status == AppointmentStatus.Confirmed)?.Count ?? 0;
        var checkedIn = counts.SingleOrDefault(x => x.Status == AppointmentStatus.CheckedIn)?.Count ?? 0;
        return new(scope.Kind, scope.Id, scope.Place?.Name ?? scope.Branch!.Name,
            scope.Place?.LifecycleState.ToString() ?? scope.Branch!.LifecycleState.ToString(), scope.Active,
            scope.Place?.ClinicBranchId, scope.Parent?.LifecycleState.ToString(), !scope.Closed && !scope.Pending && (scope.Place is null || scope.Place.Status == PracticeLocationStatus.Approved),
            confirmed, checkedIn, scope.State == 2 && confirmed == 0 && checkedIn == 0, Convert.ToBase64String(scope.Version));
    }
    private static EmergencySummary Summary(OperationalEmergency r) => new(r.Id, r.LocationId, r.BranchId, r.RequesterUserId,
        r.Category, r.Reason, r.Status, r.CreatedAtUtc, r.ReviewerUserId, r.ReviewReason, r.ReviewedAtUtc, r.CancelledCount,
        r.ReviewedPreviewHash, r.ReviewedScopeVersion is null ? null : Convert.ToBase64String(r.ReviewedScopeVersion), Convert.ToBase64String(r.Version));
    private static string PreviewHash(IEnumerable<AffectedVisit> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", rows.Select(a => $"{a.Id:N}:{a.Version}")))));
    private Task<AffectedVisit[]> Preview(Scope scope, CancellationToken ct) => Affected(scope).AsNoTracking().OrderBy(a => a.Id)
        .Select(a => new AffectedVisit(a.Id, a.SessionId, a.DoctorProfileId, a.LocationId, a.Status, a.StartsAtUtc, Convert.ToBase64String(a.Version))).ToArrayAsync(ct);
    public async Task<Result<LifecycleScopeDto>> GetAsync(OperationalScopeKind kind, Guid id, CancellationToken ct)
    {
        var actor = await Actor(false, ct); if (actor.IsError) return Result<LifecycleScopeDto>.Failure(actor.Errors);
        var scope = await Load(kind, id, false, ct);
        return scope is null || !await Authorized(scope, actor.Value, ct) ? LifecycleErrors.NotFound : await Dto(scope, ct);
    }
    public async Task<Result<LifecycleScopeDto>> ChangeAsync(OperationalScopeKind kind, Guid id, bool finalize, LifecycleChangeInput input, CancellationToken ct)
    {
        access.Fresh(); if (input is null || !(await new LifecycleChangeInputValidator().ValidateAsync(input, ct)).IsValid) return LifecycleErrors.Input;
        var actor = await Actor(false, ct); if (actor.IsError) return Result<LifecycleScopeDto>.Failure(actor.Errors);
        var snapshot = await Load(kind, id, false, ct); if (snapshot is null || !await Authorized(snapshot, actor.Value, ct)) return LifecycleErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockScope(snapshot, ct)) return LifecycleErrors.Busy;
        await HoldActor(actor.Value, ct); var liveActor = await Actor(false, ct); if (liveActor.IsError) return Result<LifecycleScopeDto>.Failure(liveActor.Errors);
        var scope = (await Load(kind, id, true, ct))!; if (!await Authorized(scope, actor.Value, ct)) return LifecycleErrors.NotFound;
        if (scope.Place?.ClinicBranchId != snapshot.Place?.ClinicBranchId) return LifecycleErrors.Stale;
        if (finalize && scope.State == 3) return await Dto(scope, ct);
        if (!WorkforceAccess.Matches(scope.Version, input.Version)) return LifecycleErrors.Stale;
        if (!finalize && scope.Closed) return LifecycleErrors.Closed;
        var outstanding = await Affected(scope).CountAsync(ct);
        var sourceVersion = scope.Version.ToArray(); var now = clock.GetUtcNow();
        var changed = scope.Place is { } p
            ? finalize ? p.FinalizeDeparture(outstanding) : p.BeginDeparture()
            : finalize ? scope.Branch!.FinalizeClosure(outstanding) : scope.Branch!.BeginClosure();
        if (changed.IsError) return Result<LifecycleScopeDto>.Failure(changed.Errors);
        if (!changed.Value) return await Dto(scope, ct);
        if (finalize)
            foreach (var session in await Sessions(scope).Where(s => s.Status == BookingSessionStatus.Published).ToArrayAsync(ct))
            {
                var retired = session.RetireForLifecycle(actor.Value, input.Reason, now, outstanding);
                if (retired.IsError) return Result<LifecycleScopeDto>.Failure(retired.Errors);
            }
        db.OperationalScopeRecords.Add(OperationalScopeRecord.Create(kind, id, actor.Value, finalize, input.Reason, sourceVersion, now));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Dto(scope, ct); }
        catch (DbUpdateConcurrencyException) { return LifecycleErrors.Stale; }
    }
    public async Task<Result<LifecyclePage<LifecycleHistoryDto>>> HistoryAsync(OperationalScopeKind kind, Guid id, int page, int size, CancellationToken ct)
    {
        if (!LifecycleValidation.Page(page, size)) return LifecycleErrors.Input;
        var owned = await GetAsync(kind, id, ct); if (owned.IsError) return Result<LifecyclePage<LifecycleHistoryDto>>.Failure(owned.Errors);
        var q = db.OperationalScopeRecords.AsNoTracking().Where(r => kind == OperationalScopeKind.Location ? r.LocationId == id : r.BranchId == id);
        var count = await q.CountAsync(ct); var rows = await q.OrderBy(r => r.RecordedAtUtc).ThenBy(r => r.AfterState).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
            .Select(r => new LifecycleHistoryDto(r.Id, r.ActorUserId, r.Action, r.Reason, r.BeforeState, r.AfterState, r.RecordedAtUtc, Convert.ToBase64String(r.SourceVersion))).ToArrayAsync(ct);
        return new LifecyclePage<LifecycleHistoryDto>(rows, count, page, size);
    }
    public async Task<Result<EmergencySummary>> RequestAsync(OperationalScopeKind kind, Guid id, EmergencyRequestInput input, CancellationToken ct)
    {
        access.Fresh(); if (input is null || !(await new EmergencyRequestInputValidator().ValidateAsync(input, ct)).IsValid) return LifecycleErrors.Input;
        var actor = await Actor(false, ct); if (actor.IsError) return Result<EmergencySummary>.Failure(actor.Errors);
        var snapshot = await Load(kind, id, false, ct); if (snapshot is null || !await Authorized(snapshot, actor.Value, ct)) return LifecycleErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct); if (!await LockScope(snapshot, ct)) return LifecycleErrors.Busy;
        await HoldActor(actor.Value, ct); var liveActor = await Actor(false, ct); if (liveActor.IsError) return Result<EmergencySummary>.Failure(liveActor.Errors);
        var scope = (await Load(kind, id, false, ct))!; if (!await Authorized(scope, actor.Value, ct)) return LifecycleErrors.NotFound;
        var pending = await db.OperationalEmergencies.AsNoTracking().SingleOrDefaultAsync(r => r.Status == EmergencyRequestStatus.Pending && (kind == OperationalScopeKind.Location ? r.LocationId == id : r.BranchId == id), ct);
        if (pending is not null) return pending.RequesterUserId == actor.Value && pending.Category == input.Category && pending.Reason == input.Reason.Trim() ? Summary(pending) : LifecycleErrors.PendingExists;
        if (!WorkforceAccess.Matches(scope.Version, input.Version)) return LifecycleErrors.Stale;
        if (scope.Closed) return LifecycleErrors.Closed;
        if (!scope.Pending) return LifecycleErrors.NotPending;
        if (!await Affected(scope).AnyAsync(ct)) return LifecycleErrors.NoAffectedVisits;
        var row = OperationalEmergency.Create(kind, id, actor.Value, input.Category, input.Reason, clock.GetUtcNow()).Value;
        db.OperationalEmergencies.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Summary(row);
    }
    public async Task<Result<LifecyclePage<EmergencySummary>>> RequestsAsync(bool admin, OperationalScopeKind? kind, Guid? scopeId, EmergencyRequestStatus? status, int page, int size, CancellationToken ct)
    {
        if (!LifecycleValidation.Page(page, size) || (status.HasValue && !Enum.IsDefined(status.Value))) return LifecycleErrors.Input;
        var actor = await Actor(admin, ct); if (actor.IsError) return Result<LifecyclePage<EmergencySummary>>.Failure(actor.Errors);
        if (!admin)
        {
            if (!kind.HasValue || !scopeId.HasValue) return LifecycleErrors.Input;
            var scope = await Load(kind.Value, scopeId.Value, false, ct);
            if (scope is null || !await Authorized(scope, actor.Value, ct)) return LifecycleErrors.NotFound;
        }
        var q = db.OperationalEmergencies.AsNoTracking().Where(r => (admin || (kind == OperationalScopeKind.Location ? r.LocationId == scopeId : r.BranchId == scopeId)) && (status == null || r.Status == status));
        var count = await q.CountAsync(ct); var rows = await q.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new LifecyclePage<EmergencySummary>(rows.Select(Summary).ToArray(), count, page, size);
    }
    public async Task<Result<EmergencyDetail>> DetailAsync(Guid id, bool admin, int page, int size, CancellationToken ct)
    {
        if (!LifecycleValidation.Page(page, size)) return LifecycleErrors.Input;
        var actor = await Actor(admin, ct); if (actor.IsError) return Result<EmergencyDetail>.Failure(actor.Errors);
        var row = await db.OperationalEmergencies.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct); if (row is null) return LifecycleErrors.NotFound;
        var scope = (await Load(row.LocationId.HasValue ? OperationalScopeKind.Location : OperationalScopeKind.Branch, row.LocationId ?? row.BranchId!.Value, false, ct))!;
        if (!admin && !await Authorized(scope, actor.Value, ct)) return LifecycleErrors.NotFound;
        var rows = await Preview(scope, ct);
        return new EmergencyDetail(Summary(row), await Dto(scope, ct), PreviewHash(rows), new(rows.Skip((page - 1) * size).Take(size).ToArray(), rows.Length, page, size));
    }
    private static string Facts(Appointment a) => JsonSerializer.Serialize(new { a.Status, a.Attendance, a.StatusBeforeCancellation, a.CancelledByUserId, a.CancelledAtUtc, a.CancellationReason, a.CancellationCategory });
    public async Task<Result<LifecyclePage<EmergencyCancellationDto>>> CancellationsAsync(Guid id, int page, int size, CancellationToken ct)
    {
        if (!LifecycleValidation.Page(page, size)) return LifecycleErrors.Input;
        var actor = await Actor(true, ct); if (actor.IsError) return Result<LifecyclePage<EmergencyCancellationDto>>.Failure(actor.Errors);
        if (!await db.OperationalEmergencies.AnyAsync(r => r.Id == id, ct)) return LifecycleErrors.NotFound;
        var q = db.EmergencyCancellationRecords.AsNoTracking().Where(r => r.RequestId == id); var count = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.RecordedAtUtc).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
            .Select(r => new EmergencyCancellationDto(r.AppointmentId, Convert.ToBase64String(r.SourceVersion), r.BeforeJson, r.AfterJson, r.RecordedAtUtc)).ToArrayAsync(ct);
        return new LifecyclePage<EmergencyCancellationDto>(rows, count, page, size);
    }
    public async Task<Result<EmergencySummary>> DecideAsync(Guid id, EmergencyDecisionInput input, CancellationToken ct)
    {
        access.Fresh(); if (input is null || !(await new EmergencyDecisionInputValidator().ValidateAsync(input, ct)).IsValid) return LifecycleErrors.Input;
        var approve = input.Approve!.Value;
        var actor = await Actor(true, ct); if (actor.IsError) return Result<EmergencySummary>.Failure(actor.Errors);
        var request = await db.OperationalEmergencies.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct); if (request is null) return LifecycleErrors.NotFound;
        var kind = request.LocationId.HasValue ? OperationalScopeKind.Location : OperationalScopeKind.Branch;
        var snapshot = (await Load(kind, request.LocationId ?? request.BranchId!.Value, false, ct))!;
        var noticeIds = new List<Guid>(); EmergencySummary result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await LockScope(snapshot, ct)) return LifecycleErrors.Busy;
            await HoldActor(actor.Value, ct); var liveActor = await Actor(true, ct); if (liveActor.IsError) return Result<EmergencySummary>.Failure(liveActor.Errors);
            request = await db.OperationalEmergencies.SingleAsync(r => r.Id == id, ct);
            if (request.RequesterUserId == actor.Value) return LifecycleErrors.SelfReview;
            if (request.Status != EmergencyRequestStatus.Pending)
                return request.Status == (approve ? EmergencyRequestStatus.Approved : EmergencyRequestStatus.Rejected) && request.ReviewReason == input.Reason.Trim() ? Summary(request) : LifecycleErrors.State;
            if (!WorkforceAccess.Matches(request.Version, input.Version)) return LifecycleErrors.Stale;
            var scope = (await Load(kind, snapshot.Id, false, ct))!;
            var preview = await Preview(scope, ct); var hash = PreviewHash(preview);
            if (approve && (!WorkforceAccess.Matches(scope.Version, input.ScopeVersion!) || hash != input.PreviewHash)) return LifecycleErrors.Stale;
            // A case can still be closed with zero cancellations after patients resolve visits or normal finalization.
            if (approve && preview.Length > 0 && (!scope.Pending || scope.Closed)) return LifecycleErrors.NotPending;
            var now = clock.GetUtcNow(); var decision = request.Decide(approve, actor.Value, input.Reason, hash, scope.Version, preview.Length, now);
            if (decision.IsError) return Result<EmergencySummary>.Failure(decision.Errors);
            if (approve)
            {
                var sessions = await Sessions(scope).ToDictionaryAsync(s => s.Id, ct);
                var rows = await Affected(scope).OrderBy(a => a.Id).ToArrayAsync(ct);
                var locations = rows.Select(a => a.LocationId).Distinct().ToArray();
                var branches = await db.DoctorPracticeLocations.Where(p => locations.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ClinicBranchId, ct);
                foreach (var a in rows)
                {
                    var before = Facts(a); var version = a.Version.ToArray();
                    var cancelled = a.CancelForEmergency(request, branches[a.LocationId], now);
                    if (cancelled.IsError) return Result<EmergencySummary>.Failure(cancelled.Errors);
                    sessions[a.SessionId].ReleaseAllocation();
                    db.EmergencyCancellationRecords.Add(EmergencyCancellationRecord.Create(request, a, version, before, Facts(a), now));
                    foreach (var recipient in new[] { a.PatientUserId, a.DoctorUserId }.Distinct())
                    {
                        var notice = BookingNotice.Create(a.Id, recipient, BookingNoticeKind.Cancelled);
                        db.BookingNotices.Add(notice); noticeIds.Add(notice.Id);
                    }
                }
            }
            try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); result = Summary(request); }
            catch (DbUpdateConcurrencyException) { return LifecycleErrors.Stale; }
        }
        await notices.DeliverAsync(noticeIds.ToArray(), CancellationToken.None);
        return result;
    }
}
