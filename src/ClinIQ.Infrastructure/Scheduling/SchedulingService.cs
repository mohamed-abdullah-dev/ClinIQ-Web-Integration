using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Scheduling;

internal sealed class SchedulingService(AppDbContext db, ICurrentUser current, WorkforceAccess access, CairoScheduleClock cairo, TimeProvider clock) : ISchedulingService
{
    private async Task<Result<DoctorProfile>> DoctorAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return ScheduleErrors.Unauthorized;
        var doctor = await access.Doctors.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct);
        return doctor is null ? ScheduleErrors.Forbidden : doctor;
    }
    private async Task<Result<Success>> LockDoctorAsync(DoctorProfile doctor, CancellationToken ct)
    {
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{doctor.Id:N}", ct)) return ScheduleErrors.Busy;
        return await access.Doctors.AnyAsync(x => x.Id == doctor.Id && x.UserId == current.UserId, ct) ? Result.Success : ScheduleErrors.Forbidden;
    }
    private IQueryable<DoctorPracticeLocation> AvailablePlaces => db.DoctorPracticeLocations.Where(p => p.IsActive && p.LifecycleState == PracticeLifecycleState.Active && p.Status == PracticeLocationStatus.Approved &&
        access.Doctors.Any(d => d.Id == p.DoctorProfileId && d.UserId == p.UserId) && (p.ClinicBranchId == null ||
        db.ClinicBranches.Any(b => b.Id == p.ClinicBranchId && b.IsActive && b.LifecycleState == Domain.Clinics.BranchLifecycleState.Open && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive))));
    private Task<DoctorPracticeLocation?> OwnPlaceAsync(Guid id, DoctorProfile doctor, CancellationToken ct) => db.DoctorPracticeLocations.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.DoctorProfileId == doctor.Id && p.UserId == doctor.UserId, ct);
    private Task<bool> HolidayAsync(Guid doctor, Guid location, DateOnly date, CancellationToken ct) => db.ScheduleExceptions.AnyAsync(x => x.DoctorProfileId == doctor && x.IsActive && x.LocalDate == date && (x.LocationId == null || x.LocationId == location), ct);
    private Task<bool> OverlapAsync(Guid doctor, SessionWindow window, Guid? except, CancellationToken ct) => db.BookingSessions.AnyAsync(x => x.DoctorProfileId == doctor && x.Status == BookingSessionStatus.Published &&
        x.Id != except && x.StartsAtUtc < window.EndsAtUtc && window.StartsAtUtc < x.EndsAtUtc, ct);
    private async Task<bool> RuleOverlapAsync(Guid doctor, ScheduleRuleData data, Guid? except, CancellationToken ct)
    {
        if (!data.IsActive) return false;
        var rows = await db.ScheduleRules.AsNoTracking().Where(r => r.DoctorProfileId == doctor && r.IsActive && r.Id != except && r.Day == data.Day).ToArrayAsync(ct);
        return rows.Any(x => data.Overlaps(x.Data));
    }
    private Task<bool> SessionLockAsync(Guid id, CancellationToken ct) => access.LockAsync($"ClinIQ.BookingSession.{id:N}", ct);

    public async Task<Result<ScheduleRuleDto>> CreateRuleAsync(Guid locationId, ScheduleRuleData input, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleRuleDto>.Failure(doctor.Errors);
        var valid = input.Validate(); if (valid.IsError) return Result<ScheduleRuleDto>.Failure(valid.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<ScheduleRuleDto>.Failure(locked.Errors);
        var place = await OwnPlaceAsync(locationId, doctor.Value, ct); if (place is null) return ScheduleErrors.NotFound;
        if (!await AvailablePlaces.AnyAsync(p => p.Id == locationId, ct)) return ScheduleErrors.Unavailable;
        if (await RuleOverlapAsync(doctor.Value.Id, input, null, ct)) return ScheduleErrors.Overlap;
        var row = ScheduleRule.Create(locationId, doctor.Value.Id, doctor.Value.UserId, input).Value;
        db.ScheduleRules.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row);
    }
    public async Task<Result<ScheduleRuleDto>> UpdateRuleAsync(Guid id, ScheduleRuleData input, string version, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleRuleDto>.Failure(doctor.Errors);
        var valid = input.Validate(); if (valid.IsError) return Result<ScheduleRuleDto>.Failure(valid.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<ScheduleRuleDto>.Failure(locked.Errors);
        var row = await db.ScheduleRules.SingleOrDefaultAsync(r => r.Id == id && r.DoctorProfileId == doctor.Value.Id && r.UserId == doctor.Value.UserId, ct);
        if (row is null) return ScheduleErrors.NotFound;
        if (!WorkforceAccess.Matches(row.Version, version)) return ScheduleErrors.Stale;
        if (input.IsActive && !await AvailablePlaces.AnyAsync(p => p.Id == row.LocationId, ct)) return ScheduleErrors.Unavailable;
        if (await RuleOverlapAsync(doctor.Value.Id, input, id, ct)) return ScheduleErrors.Overlap;
        row.Update(input);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row); } catch (DbUpdateConcurrencyException) { return ScheduleErrors.Stale; }
    }
    public async Task<Result<PublicationResult>> PublishAsync(Guid locationId, DateOnly from, DateOnly until, bool preview, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<PublicationResult>.Failure(doctor.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<PublicationResult>.Failure(locked.Errors);
        if (await OwnPlaceAsync(locationId, doctor.Value, ct) is null) return ScheduleErrors.NotFound;
        if (!await AvailablePlaces.AnyAsync(p => p.Id == locationId, ct)) return ScheduleErrors.Unavailable;
        if (!cairo.InHorizon(from, until)) return ScheduleErrors.Horizon;
        var operationalBranchId = await db.DoctorPracticeLocations.Where(p => p.Id == locationId).Select(p => p.ClinicBranchId).SingleAsync(ct);
        var now = clock.GetUtcNow();
        var rules = await db.ScheduleRules.AsNoTracking().Where(x => x.LocationId == locationId && x.IsActive && x.EffectiveFrom <= until && (x.EffectiveUntil == null || x.EffectiveUntil >= from)).OrderBy(x => x.Id).ToArrayAsync(ct);
        var existing = await db.BookingSessions.AsNoTracking().Where(x => x.LocationId == locationId && x.SourceRuleId != null && x.LocalDate >= from && x.LocalDate <= until).ToArrayAsync(ct);
        var byOccurrence = existing.ToDictionary(x => (x.SourceRuleId!.Value, x.LocalDate));
        var occupied = await db.BookingSessions.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.Status == BookingSessionStatus.Published && x.EndsAtUtc > now).Select(x => new { x.StartsAtUtc, x.EndsAtUtc }).ToArrayAsync(ct);
        var intervals = occupied.Select(x => (Start: x.StartsAtUtc, End: x.EndsAtUtc)).ToList();
        var holidays = await db.ScheduleExceptions.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.IsActive && x.LocalDate >= from && x.LocalDate <= until && (x.LocationId == null || x.LocationId == locationId)).Select(x => x.LocalDate).ToArrayAsync(ct);
        var items = new List<PublicationItem>(); var created = new List<BookingSession>();
        for (var dayNumber = from.DayNumber; dayNumber <= until.DayNumber; dayNumber++)
        {
            var date = DateOnly.FromDayNumber(dayNumber);
            foreach (var rule in rules.Where(r => r.Data.OccursOn(date)))
            {
                if (byOccurrence.TryGetValue((rule.Id, date), out var prior))
                {
                    items.Add(new(prior.Id, rule.Id, Convert.ToBase64String(prior.SourceRuleVersion!), date, prior.StartsAtUtc, prior.EndsAtUtc, prior.BookingClosesAtUtc,
                        prior.MaxCapacity, prior.BookingCutoffMinutes, prior.BookingClosesAtUtc <= prior.StartsAtUtc, "Preserved")); continue;
                }
                if (holidays.Contains(date))
                {
                    // A suppressed occurrence needs no UTC conversion, including a DST gap on a holiday.
                    items.Add(new(null, rule.Id, Convert.ToBase64String(rule.Version), date, null, null, null, rule.MaxCapacity, rule.BookingCutoffMinutes, null, "Suppressed")); continue;
                }
                var window = cairo.Window(date, rule.StartsLocal, rule.EndsLocal); if (window.IsError) return Result<PublicationResult>.Failure(window.Errors);
                var closing = BookingSession.Closing(window.Value.EndsAtUtc, rule.BookingCutoffMinutes); if (closing.IsError) return Result<PublicationResult>.Failure(closing.Errors);
                var action = window.Value.StartsAtUtc <= now ? "Past" : preview ? "WouldCreate" : "Created";
                Guid? sessionId = null;
                if (action is "WouldCreate" or "Created")
                {
                    if (intervals.Any(x => x.Start < window.Value.EndsAtUtc && window.Value.StartsAtUtc < x.End)) return ScheduleErrors.Overlap;
                    var row = BookingSession.Create(locationId, doctor.Value.Id, doctor.Value.UserId, window.Value, rule.MaxCapacity, rule.BookingCutoffMinutes, now, rule.Id, rule.Version, operationalBranchId);
                    if (row.IsError) return Result<PublicationResult>.Failure(row.Errors);
                    if (!preview) sessionId = row.Value.Id;
                    created.Add(row.Value); intervals.Add((window.Value.StartsAtUtc, window.Value.EndsAtUtc));
                }
                items.Add(new(sessionId, rule.Id, Convert.ToBase64String(rule.Version), date, window.Value.StartsAtUtc, window.Value.EndsAtUtc, closing.Value, rule.MaxCapacity,
                    rule.BookingCutoffMinutes, closing.Value <= window.Value.StartsAtUtc, action));
            }
        }
        if (!preview) { db.BookingSessions.AddRange(created); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        return new PublicationResult(items, preview, created.Count, preview ? 0 : created.Count, items.Count(x => x.Action == "Preserved"), items.Count(x => x.Action == "Suppressed"), items.Count(x => x.Action == "Past"));
    }
    public async Task<Result<SessionDto>> ExtraAsync(Guid locationId, DateOnly date, TimeOnly start, TimeOnly end, int capacity, int cutoff, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SessionDto>.Failure(doctor.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<SessionDto>.Failure(locked.Errors);
        if (await OwnPlaceAsync(locationId, doctor.Value, ct) is null) return ScheduleErrors.NotFound;
        if (!await AvailablePlaces.AnyAsync(x => x.Id == locationId, ct)) return ScheduleErrors.Unavailable;
        if (!cairo.InHorizon(date, date)) return ScheduleErrors.Horizon;
        if (await HolidayAsync(doctor.Value.Id, locationId, date, ct)) return ScheduleErrors.Holiday;
        var window = cairo.Window(date, start, end); if (window.IsError) return Result<SessionDto>.Failure(window.Errors);
        if (await OverlapAsync(doctor.Value.Id, window.Value, null, ct)) return ScheduleErrors.Overlap;
        var operationalBranchId = await db.DoctorPracticeLocations.Where(p => p.Id == locationId).Select(p => p.ClinicBranchId).SingleAsync(ct);
        var row = BookingSession.Create(locationId, doctor.Value.Id, doctor.Value.UserId, window.Value, capacity, cutoff, clock.GetUtcNow(), operationalBranchId: operationalBranchId);
        if (row.IsError) return Result<SessionDto>.Failure(row.Errors);
        db.BookingSessions.Add(row.Value); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row.Value, true);
    }
    public async Task<Result<SessionDto>> EditSessionAsync(Guid id, SessionEditKind kind, string version, TimeOnly? start, TimeOnly? end, int? value, string? reason, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SessionDto>.Failure(doctor.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<SessionDto>.Failure(locked.Errors);
        if (!await SessionLockAsync(id, ct)) return ScheduleErrors.Busy;
        var row = await db.BookingSessions.SingleOrDefaultAsync(x => x.Id == id && x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId, ct);
        if (row is null) return ScheduleErrors.NotFound;
        if (!WorkforceAccess.Matches(row.Version, version)) return ScheduleErrors.Stale;
        var available = await AvailablePlaces.AnyAsync(x => x.Id == row.LocationId, ct) && !await HolidayAsync(doctor.Value.Id, row.LocationId, row.LocalDate, ct);
        Result<Success> changed;
        if (kind == SessionEditKind.Times)
        {
            if (!available) return ScheduleErrors.Unavailable;
            if (!start.HasValue || !end.HasValue) return ScheduleErrors.LocalTimes;
            var window = cairo.Window(row.LocalDate, start.Value, end.Value); if (window.IsError) return Result<SessionDto>.Failure(window.Errors);
            if (await OverlapAsync(doctor.Value.Id, window.Value, id, ct)) return ScheduleErrors.Overlap;
            changed = row.ChangeTimes(window.Value, clock.GetUtcNow());
        }
        else changed = kind switch
        {
            SessionEditKind.Capacity => row.ChangeCapacity(value ?? 0, clock.GetUtcNow()),
            SessionEditKind.Cutoff => row.ChangeCutoff(value ?? -1, clock.GetUtcNow()),
            SessionEditKind.Pause => row.Pause(true, available, clock.GetUtcNow()),
            SessionEditKind.Resume => row.Pause(false, available, clock.GetUtcNow()),
            SessionEditKind.Cancel => row.Cancel(doctor.Value.UserId, reason ?? "", clock.GetUtcNow()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (changed.IsError) return Result<SessionDto>.Failure(changed.Errors);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row, available); } catch (DbUpdateConcurrencyException) { return ScheduleErrors.Stale; }
    }
    public async Task<Result<ScheduleExceptionDto>> CreateExceptionAsync(Guid? locationId, DateOnly date, string reason, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleExceptionDto>.Failure(doctor.Errors);
        if (date < cairo.Today) return ScheduleErrors.DateRange;
        var exception = ScheduleException.Create(doctor.Value.Id, doctor.Value.UserId, locationId, date, reason); if (exception.IsError) return Result<ScheduleExceptionDto>.Failure(exception.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<ScheduleExceptionDto>.Failure(locked.Errors);
        if (date < cairo.Today) return ScheduleErrors.DateRange;
        if (locationId.HasValue && await OwnPlaceAsync(locationId.Value, doctor.Value, ct) is null) return ScheduleErrors.NotFound;
        if (await db.ScheduleExceptions.AnyAsync(x => x.DoctorProfileId == doctor.Value.Id && x.LocationId == locationId && x.LocalDate == date && x.IsActive, ct)) return ScheduleErrors.DuplicateException;
        var ids = await db.BookingSessions.Where(x => x.DoctorProfileId == doctor.Value.Id && x.LocalDate == date && x.Status == BookingSessionStatus.Published &&
            (locationId == null || x.LocationId == locationId) && x.EndsAtUtc > clock.GetUtcNow()).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in ids)
        {
            if (!await SessionLockAsync(id, ct)) return ScheduleErrors.Busy;
            var session = await db.BookingSessions.SingleAsync(x => x.Id == id, ct);
            if (clock.GetUtcNow() >= session.StartsAtUtc) return ScheduleErrors.Started;
            var cancelled = session.Cancel(doctor.Value.UserId, reason, clock.GetUtcNow()); if (cancelled.IsError) return Result<ScheduleExceptionDto>.Failure(cancelled.Errors);
        }
        db.ScheduleExceptions.Add(exception.Value);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(exception.Value); } catch (DbUpdateConcurrencyException) { return ScheduleErrors.Stale; }
    }
    public async Task<Result<ScheduleExceptionDto>> RemoveExceptionAsync(Guid id, string version, string reason, CancellationToken ct)
    {
        access.Fresh(); var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleExceptionDto>.Failure(doctor.Errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockDoctorAsync(doctor.Value, ct); if (locked.IsError) return Result<ScheduleExceptionDto>.Failure(locked.Errors);
        var row = await db.ScheduleExceptions.SingleOrDefaultAsync(x => x.Id == id && x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId, ct);
        if (row is null) return ScheduleErrors.NotFound;
        if (!WorkforceAccess.Matches(row.Version, version)) return ScheduleErrors.Stale;
        var removed = row.Remove(doctor.Value.UserId, reason, clock.GetUtcNow()); if (removed.IsError) return Result<ScheduleExceptionDto>.Failure(removed.Errors);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(row); } catch (DbUpdateConcurrencyException) { return ScheduleErrors.Stale; }
    }
    public async Task<Result<ScheduleRuleDto>> GetRuleAsync(Guid id, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleRuleDto>.Failure(doctor.Errors);
        var row = await db.ScheduleRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId, ct);
        return row is null ? ScheduleErrors.NotFound : Dto(row);
    }
    public async Task<Result<SchedulePage<ScheduleRuleDto>>> RulesAsync(Guid? locationId, int page, int pageSize, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SchedulePage<ScheduleRuleDto>>.Failure(doctor.Errors);
        var query = db.ScheduleRules.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId && (locationId == null || x.LocationId == locationId));
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.LocationId).ThenBy(x => x.Day).ThenBy(x => x.StartsLocal).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new SchedulePage<ScheduleRuleDto>(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    public async Task<Result<SessionDto>> GetSessionAsync(Guid id, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SessionDto>.Failure(doctor.Errors);
        var row = await db.BookingSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId, ct);
        if (row is null) return ScheduleErrors.NotFound;
        var available = await AvailablePlaces.AnyAsync(x => x.Id == row.LocationId, ct) && !await HolidayAsync(doctor.Value.Id, row.LocationId, row.LocalDate, ct);
        return Dto(row, available);
    }
    public async Task<Result<SchedulePage<SessionDto>>> SessionsAsync(Guid? locationId, DateOnly from, DateOnly until, int page, int pageSize, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SchedulePage<SessionDto>>.Failure(doctor.Errors);
        var query = db.BookingSessions.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId && (locationId == null || x.LocationId == locationId) && x.LocalDate >= from && x.LocalDate <= until);
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.StartsAtUtc).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        var locations = rows.Select(x => x.LocationId).Distinct().ToArray(); var available = await AvailablePlaces.Where(x => locations.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(ct);
        var holidays = await db.ScheduleExceptions.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.IsActive && x.LocalDate >= from && x.LocalDate <= until).ToArrayAsync(ct);
        return new SchedulePage<SessionDto>(rows.Select(x => Dto(x, available.Contains(x.LocationId) && !holidays.Any(h => h.LocalDate == x.LocalDate && (h.LocationId == null || h.LocationId == x.LocationId)))).ToArray(), count, page, pageSize);
    }
    public async Task<Result<ScheduleExceptionDto>> GetExceptionAsync(Guid id, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<ScheduleExceptionDto>.Failure(doctor.Errors);
        var row = await db.ScheduleExceptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId, ct);
        return row is null ? ScheduleErrors.NotFound : Dto(row);
    }
    public async Task<Result<SchedulePage<ScheduleExceptionDto>>> ExceptionsAsync(DateOnly from, DateOnly until, int page, int pageSize, CancellationToken ct)
    {
        var doctor = await DoctorAsync(ct); if (doctor.IsError) return Result<SchedulePage<ScheduleExceptionDto>>.Failure(doctor.Errors);
        var query = db.ScheduleExceptions.AsNoTracking().Where(x => x.DoctorProfileId == doctor.Value.Id && x.UserId == doctor.Value.UserId && x.LocalDate >= from && x.LocalDate <= until);
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.LocalDate).ThenBy(x => x.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new SchedulePage<ScheduleExceptionDto>(rows.Select(Dto).ToArray(), count, page, pageSize);
    }
    private static ScheduleRuleDto Dto(ScheduleRule r) => new(r.Id, r.LocationId, r.Data, Convert.ToBase64String(r.Version));
    private static ScheduleExceptionDto Dto(ScheduleException r) => new(r.Id, r.LocationId, r.LocalDate, r.Reason, r.IsActive, Convert.ToBase64String(r.Version), r.RemovalReason, r.RemovedByUserId, r.RemovedAtUtc);
    private SessionDto Dto(BookingSession s, bool available)
    {
        var now = clock.GetUtcNow();
        return new(s.Id, s.LocationId, s.DoctorProfileId, s.SourceRuleId, s.SourceRuleVersion is null ? null : Convert.ToBase64String(s.SourceRuleVersion), s.LocalDate, s.StartsLocal, s.EndsLocal, s.TimeZoneId, s.StartsAtUtc, s.EndsAtUtc,
            s.MaxCapacity, s.AllocatedCount, s.BookingCutoffMinutes, s.BookingClosesAtUtc, s.BookingPaused, s.Status.ToString(), available, s.CanAllocate(now, available), s.BookingClosesAtUtc <= s.StartsAtUtc, now >= s.EndsAtUtc, Convert.ToBase64String(s.Version), s.CancelledByUserId, s.CancelledAtUtc, s.CancellationReason);
    }
}
