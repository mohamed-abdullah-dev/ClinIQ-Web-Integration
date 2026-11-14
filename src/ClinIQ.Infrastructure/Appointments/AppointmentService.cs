using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Patients;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using ClinIQ.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Appointments;

internal sealed class AppointmentService(AppDbContext db, ICurrentUser current, WorkforceAccess access, IPublicDirectoryService directory,
    BookingQuoteProtector quotes, BookingNoticeDelivery notices, TimeProvider clock, ISchedulingService scheduling) : IAppointmentService
{
    private async Task<Result<PatientProfile>> PatientAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return AppointmentErrors.Unauthorized;
        if (!await RoleAsync(id, "PATIENT", ct)) return AppointmentErrors.Forbidden;
        var profile = await db.PatientProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == id, ct);
        return profile is null ? PatientProfileErrors.NotFound : profile;
    }
    private Task<bool> RoleAsync(Guid id, string role, CancellationToken ct) => db.Users.AnyAsync(u => u.Id == id && u.EmailConfirmed &&
        db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == role)), ct);
    private static AppointmentSelection Selection(PublicSessionProfile p, PatientProfile patient) => new(p.Session.Id, p.Place.Id, p.Doctor.Id,
        p.Place.ClinicId, p.Place.BranchId, p.Doctor.ProfessionalName, p.Doctor.ProfessionalGrade, p.Doctor.Specialty.Id, p.Doctor.Specialty.NameAr, p.Doctor.Specialty.NameEn,
        p.Place.Name, p.Place.Address, p.Session.LocalDate, p.Session.StartsLocal, p.Session.EndsLocal, p.Session.TimeZoneId,
        p.Session.StartsAtUtc, p.Session.EndsAtUtc, p.Session.BookingClosesAtUtc, p.Place.ConsultationFee, p.Place.Currency, patient.FullName, patient.PhoneNumber);
    private Task<OverlappingAppointment[]> OverlapsAsync(Guid patient, AppointmentSelection s, Guid? source, CancellationToken ct) => db.Appointments.AsNoTracking()
        .Where(a => a.PatientId == patient && a.Id != source && a.DoctorProfileId != s.DoctorProfileId &&
            (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn) && a.StartsAtUtc < s.EndsAtUtc && s.StartsAtUtc < a.EndsAtUtc)
        .OrderBy(a => a.Id).Select(a => new OverlappingAppointment(a.Id, a.DoctorProfileId, a.DoctorName, a.StartsAtUtc, a.EndsAtUtc)).ToArrayAsync(ct);
    private static Error? SourceError(Appointment? source, AppointmentSelection target, Guid actor, DateTimeOffset now)
    {
        if (source is null) return AppointmentErrors.NotFound;
        var allowed = source.CanChangeByPatient(actor, now); if (allowed.IsError) return allowed.Errors[0];
        return source.DoctorProfileId != target.DoctorProfileId || source.SessionId == target.SessionId ? AppointmentErrors.SameDoctor : null;
    }
    public async Task<Result<BookingPreview>> PreviewAsync(Guid sessionId, Guid? sourceId, CancellationToken ct)
    {
        var patient = await PatientAsync(ct); if (patient.IsError) return Result<BookingPreview>.Failure(patient.Errors);
        var target = await directory.SessionAsync(sessionId, ct); if (target.IsError) return AppointmentErrors.Closed;
        var selection = Selection(target.Value, patient.Value);
        if (sourceId.HasValue)
        {
            var source = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == sourceId && a.PatientId == patient.Value.Id, ct);
            if (SourceError(source, selection, patient.Value.UserId, clock.GetUtcNow()) is { } error) return error;
        }
        var overlaps = await OverlapsAsync(patient.Value.Id, selection, sourceId, ct);
        var expiry = clock.GetUtcNow().AddMinutes(10);
        var proof = quotes.Protect(new(patient.Value.Id, sessionId, sourceId, BookingQuoteProtector.Hash(selection), BookingQuoteProtector.Hash(overlaps), expiry));
        return new BookingPreview(selection, overlaps, overlaps.Length == 0 ? null : "This overlaps another doctor's session. Queue waits and travel may prevent attending both; attendance is not guaranteed.", proof, expiry);
    }
    private async Task<bool> LockPatientAsync(Guid user, CancellationToken ct) =>
        await access.LockAsync($"ClinIQ.PatientBookings.{user:N}", ct) && await access.LockAsync($"ClinIQ.PatientProfile.{user:N}", ct);
    private async Task<bool> LockSessionsAsync(Guid doctor, IEnumerable<Guid> sessions, CancellationToken ct)
    {
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{doctor:N}", ct)) return false;
        foreach (var id in sessions.Distinct().Order()) if (!await access.LockAsync($"ClinIQ.BookingSession.{id:N}", ct)) return false;
        return true;
    }
    private async Task FreezeEligibilityAsync(BookingSession s, Guid patientUser, CancellationToken ct)
    {
        // Shared price lock, plus transaction-held row/range locks for the live eligibility read.
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorPracticeLocations WITH (UPDLOCK,HOLDLOCK) WHERE Id={s.LocationId}").ToArrayAsync(ct);
        var doctor = await db.DoctorProfiles.AsNoTracking().SingleAsync(d => d.Id == s.DoctorProfileId, ct);
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorProfiles WITH (UPDLOCK,HOLDLOCK) WHERE Id={doctor.Id}").ToArrayAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM DoctorRegistrations WITH (UPDLOCK,HOLDLOCK) WHERE Id={doctor.DoctorRegistrationId}").ToArrayAsync(ct);
        foreach (var user in new[] { patientUser, s.UserId }.Distinct().Order())
        {
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH (HOLDLOCK) WHERE Id={user}").ToArrayAsync(ct);
            await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH (HOLDLOCK) WHERE UserId={user}").ToArrayAsync(ct);
        }
        var branchId = await db.DoctorPracticeLocations.Where(p => p.Id == s.LocationId).Select(p => p.ClinicBranchId).SingleAsync(ct);
        if (branchId.HasValue)
        {
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM ClinicBranches WITH (UPDLOCK,HOLDLOCK) WHERE Id={branchId.Value}").ToArrayAsync(ct);
            var clinic = await db.ClinicBranches.Where(b => b.Id == branchId).Select(b => b.ClinicId).SingleAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM Clinics WITH (UPDLOCK,HOLDLOCK) WHERE Id={clinic}").ToArrayAsync(ct);
        }
    }
    private async Task<Result<AppointmentOperationResult>?> ReplayAsync(Guid patient, Guid request, string hash, CancellationToken ct)
    {
        var op = await db.AppointmentOperations.AsNoTracking().SingleOrDefaultAsync(o => o.PatientId == patient && o.RequestId == request, ct);
        if (op is null) return null;
        if (op.RequestHash != hash) return Result<AppointmentOperationResult>.Failure(AppointmentErrors.RequestReuse);
        var row = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == op.ResultAppointmentId && a.PatientId == patient, ct);
        return new AppointmentOperationResult(await DtoAsync(row, ct), true);
    }
    public Task<Result<AppointmentOperationResult>> BookAsync(BookingConfirmation input, CancellationToken ct) => ConfirmAsync(null, input, ct);
    public Task<Result<AppointmentOperationResult>> RescheduleAsync(Guid id, BookingConfirmation input, CancellationToken ct) => ConfirmAsync(id, input, ct);
    private async Task<Result<AppointmentOperationResult>> ConfirmAsync(Guid? sourceId, BookingConfirmation input, CancellationToken ct)
    {
        access.Fresh(); var patient = await PatientAsync(ct); if (patient.IsError) return Result<AppointmentOperationResult>.Failure(patient.Errors);
        var kind = sourceId.HasValue ? AppointmentOperationKind.Reschedule : AppointmentOperationKind.Book;
        var hash = BookingQuoteProtector.Hash(new { Kind = kind, SourceId = sourceId, Input = input });
        var noticeIds = new List<Guid>(); Appointment result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await LockPatientAsync(patient.Value.UserId, ct)) return AppointmentErrors.Busy;
            var replay = await ReplayAsync(patient.Value.Id, input.RequestId, hash, ct); if (replay is not null) return replay;
            if (!input.AcceptPrice || !input.ConfirmContact) return AppointmentErrors.Acceptance;
            var proof = quotes.Read(input.QuoteToken);
            if (proof is null || proof.PatientId != patient.Value.Id || proof.SessionId != input.SessionId || proof.SourceId != sourceId || proof.ExpiresAtUtc <= clock.GetUtcNow()) return AppointmentErrors.Quote;
            var snapshot = await db.BookingSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == input.SessionId, ct); if (snapshot is null) return AppointmentErrors.Closed;
            var sourceSnapshot = sourceId.HasValue ? await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == sourceId && a.PatientId == patient.Value.Id, ct) : null;
            if (sourceId.HasValue && sourceSnapshot is null) return AppointmentErrors.NotFound;
            if (!await LockSessionsAsync(snapshot.DoctorProfileId, sourceSnapshot is null ? [snapshot.Id] : [snapshot.Id, sourceSnapshot.SessionId], ct)) return AppointmentErrors.Busy;
            if (!await access.LockAsync($"ClinIQ.PracticeLocation.{snapshot.LocationId:N}", ct)) return AppointmentErrors.Busy;
            await FreezeEligibilityAsync(snapshot, patient.Value.UserId, ct);
            patient = await PatientAsync(ct); if (patient.IsError) return Result<AppointmentOperationResult>.Failure(patient.Errors);
            var target = await directory.SessionAsync(input.SessionId, ct); if (target.IsError) return AppointmentErrors.Closed;
            var selection = Selection(target.Value, patient.Value);
            if (proof.SelectionHash != BookingQuoteProtector.Hash(selection)) return AppointmentErrors.SelectionChanged;
            var source = sourceId.HasValue ? await db.Appointments.SingleAsync(a => a.Id == sourceId && a.PatientId == patient.Value.Id, ct) : null;
            if (sourceId.HasValue && SourceError(source, selection, patient.Value.UserId, clock.GetUtcNow()) is { } sourceError) return sourceError;
            if (await db.Appointments.AnyAsync(a => a.PatientId == patient.Value.Id && a.SessionId == input.SessionId &&
                (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.NoShow || a.Status == AppointmentStatus.AttendedNotSeen), ct)) return AppointmentErrors.Duplicate;
            var overlaps = await OverlapsAsync(patient.Value.Id, selection, sourceId, ct);
            if (proof.OverlapHash != BookingQuoteProtector.Hash(overlaps)) return AppointmentErrors.SelectionChanged;
            if (overlaps.Length > 0 && !input.AcknowledgeOverlap) return AppointmentErrors.Overlap;
            var session = await db.BookingSessions.SingleAsync(s => s.Id == input.SessionId, ct);
            var now = clock.GetUtcNow();
            if (proof.ExpiresAtUtc <= now) return AppointmentErrors.Quote;
            var allocation = session.Allocate(now, true); if (allocation.IsError) return AppointmentErrors.Closed;
            var made = Appointment.Create(patient.Value.Id, patient.Value.UserId, session.UserId, selection, sourceId);
            if (made.IsError) return Result<AppointmentOperationResult>.Failure(made.Errors);
            result = made.Value;
            if (source is not null)
            {
                var changed = source.Reschedule(patient.Value.UserId, now); if (changed.IsError) return Result<AppointmentOperationResult>.Failure(changed.Errors);
                var oldSession = await db.BookingSessions.SingleAsync(s => s.Id == source.SessionId, ct); oldSession.ReleaseAllocation();
                AddNotices(source, BookingNoticeKind.Rescheduled, noticeIds);
            }
            else AddNotices(result, BookingNoticeKind.Booked, noticeIds);
            db.Appointments.Add(result);
            db.AppointmentOperations.Add(AppointmentOperation.Create(patient.Value.Id, input.RequestId, kind, hash, result.Id));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        await notices.DeliverAsync(noticeIds.ToArray(), CancellationToken.None);
        return new AppointmentOperationResult(await DtoAsync(result, CancellationToken.None), false);
    }
    public async Task<Result<AppointmentOperationResult>> CancelAsync(Guid id, Guid requestId, string reason, CancellationToken ct)
    {
        access.Fresh(); var patient = await PatientAsync(ct); if (patient.IsError) return Result<AppointmentOperationResult>.Failure(patient.Errors);
        var hash = BookingQuoteProtector.Hash(new { Kind = AppointmentOperationKind.Cancel, Id = id, Reason = reason });
        var noticeIds = new List<Guid>(); Appointment row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await LockPatientAsync(patient.Value.UserId, ct)) return AppointmentErrors.Busy;
            var replay = await ReplayAsync(patient.Value.Id, requestId, hash, ct); if (replay is not null) return replay;
            var snapshot = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.PatientId == patient.Value.Id, ct); if (snapshot is null) return AppointmentErrors.NotFound;
            if (!await LockSessionsAsync(snapshot.DoctorProfileId, [snapshot.SessionId], ct)) return AppointmentErrors.Busy;
            row = await db.Appointments.SingleAsync(a => a.Id == id && a.PatientId == patient.Value.Id, ct);
            var wasCancelled = row.Status == AppointmentStatus.Cancelled;
            var changed = row.CancelByPatient(patient.Value.UserId, reason, clock.GetUtcNow()); if (changed.IsError) return Result<AppointmentOperationResult>.Failure(changed.Errors);
            if (!wasCancelled)
            {
                var session = await db.BookingSessions.SingleAsync(s => s.Id == row.SessionId, ct); session.ReleaseAllocation();
                AddNotices(row, BookingNoticeKind.Cancelled, noticeIds);
            }
            db.AppointmentOperations.Add(AppointmentOperation.Create(patient.Value.Id, requestId, AppointmentOperationKind.Cancel, hash, row.Id));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        await notices.DeliverAsync(noticeIds.ToArray(), CancellationToken.None);
        return new AppointmentOperationResult(await DtoAsync(row, CancellationToken.None), false);
    }
    public async Task<Result<AppointmentDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var patient = await PatientAsync(ct); if (patient.IsError) return Result<AppointmentDto>.Failure(patient.Errors);
        var row = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.PatientId == patient.Value.Id, ct);
        return row is null ? AppointmentErrors.NotFound : await DtoAsync(row, ct);
    }
    public async Task<Result<AppointmentPage>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var patient = await PatientAsync(ct); if (patient.IsError) return Result<AppointmentPage>.Failure(patient.Errors);
        var query = db.Appointments.AsNoTracking().Where(a => a.PatientId == patient.Value.Id); var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.StartsAtUtc).ThenBy(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        var ids = rows.Select(a => a.Id).ToArray();
        var links = await db.Appointments.Where(a => a.PatientId == patient.Value.Id && a.ReplacesAppointmentId != null && ids.Contains(a.ReplacesAppointmentId.Value)).Select(a => new { a.ReplacesAppointmentId, a.Id }).ToDictionaryAsync(a => a.ReplacesAppointmentId!.Value, a => a.Id, ct);
        return new AppointmentPage(rows.Select(a => Dto(a, links.TryGetValue(a.Id, out var next) ? next : null)).ToArray(), count, page, pageSize);
    }
    private async Task<AppointmentDto> DtoAsync(Appointment row, CancellationToken ct) => Dto(row,
        await db.Appointments.Where(a => a.ReplacesAppointmentId == row.Id && a.PatientId == row.PatientId).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct));
    private static AppointmentDto Dto(Appointment a, Guid? next) => new(a.Id, a.Selection, a.Status.ToString(), a.ReplacesAppointmentId, next, a.CreatedAtUtc,
        a.CancellationReason, a.CancellationCategory, a.StatusBeforeCancellation?.ToString(), Convert.ToBase64String(a.Version), a.QueueNumber, a.ArrivedAtUtc, a.CompletedAtUtc);
    private void AddNotices(Appointment a, BookingNoticeKind kind, List<Guid> ids)
    {
        foreach (var recipient in new[] { a.PatientUserId, a.DoctorUserId }.Distinct())
        { var notice = BookingNotice.Create(a.Id, recipient, kind); db.BookingNotices.Add(notice); ids.Add(notice.Id); }
    }
    public async Task<Result<SessionDto>> CancelSessionAsync(Guid id, string version, string reason, CancellationToken ct)
    {
        access.Fresh(); if (current.UserId is not Guid actor) return AppointmentErrors.Unauthorized;
        var doctor = await access.Doctors.AsNoTracking().SingleOrDefaultAsync(d => d.UserId == actor, ct); if (doctor is null) return AppointmentErrors.Forbidden;
        var noticeIds = new List<Guid>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await LockSessionsAsync(doctor.Id, [id], ct)) return AppointmentErrors.Busy;
            var session = await db.BookingSessions.SingleOrDefaultAsync(s => s.Id == id && s.DoctorProfileId == doctor.Id && s.UserId == actor, ct); if (session is null) return AppointmentErrors.NotFound;
            if (!WorkforceAccess.Matches(session.Version, version)) return AppointmentErrors.Stale;
            if (session.Status == BookingSessionStatus.Published)
            {
                if (!await access.Doctors.AnyAsync(d => d.Id == doctor.Id && d.UserId == actor, ct)) return AppointmentErrors.Forbidden;
                var pending = await db.DoctorPracticeLocations.AnyAsync(p => p.Id == session.LocationId &&
                    (p.LifecycleState == Domain.PracticeLocations.PracticeLifecycleState.PendingDeparture ||
                    (p.ClinicBranchId != null && db.ClinicBranches.Any(b => b.Id == p.ClinicBranchId && b.LifecycleState == Domain.Clinics.BranchLifecycleState.PendingClosure))), ct);
                if (pending && await db.Appointments.AnyAsync(a => a.SessionId == id && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn), ct))
                    return Domain.PracticeLifecycle.LifecycleErrors.EmergencyRequired;
                var cancelled = session.CancelForDisruption(actor, reason, clock.GetUtcNow()); if (cancelled.IsError) return Result<SessionDto>.Failure(cancelled.Errors);
                var rows = await db.Appointments.Where(a => a.SessionId == id && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.CheckedIn)).ToArrayAsync(ct);
                foreach (var a in rows) { var changed = a.CancelByDoctor(actor, reason, clock.GetUtcNow()); if (changed.IsError) return Result<SessionDto>.Failure(changed.Errors); session.ReleaseAllocation(); AddNotices(a, BookingNoticeKind.Cancelled, noticeIds); }
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await notices.DeliverAsync(noticeIds.ToArray(), CancellationToken.None);
        // Scheduling owns the existing private response contract and availability calculation.
        return await scheduling.GetSessionAsync(id, CancellationToken.None);
    }
    public async Task<Result<BookingNoticeRetryResult>> RetryNoticesAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return AppointmentErrors.Unauthorized;
        if (!await RoleAsync(id, "ADMIN", ct)) return AppointmentErrors.Forbidden;
        var before = clock.GetUtcNow().AddMinutes(-1);
        var ids = await db.BookingNotices.Where(n => n.DeliveryStatus != "DevelopmentFile" && (n.LastAttemptUtc == null || n.LastAttemptUtc <= before))
            .OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id).Take(20).Select(n => n.Id).ToArrayAsync(ct);
        return await notices.DeliverAsync(ids, ct);
    }
}
