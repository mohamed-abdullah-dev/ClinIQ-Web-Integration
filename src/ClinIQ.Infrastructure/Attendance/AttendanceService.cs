using System.Text.Json;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Attendance;

internal sealed class AttendanceService(AppDbContext db, ICurrentUser current, WorkforceAccess access, TimeProvider clock) : IAttendanceService
{
    private async Task<Result<Guid>> ActorAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid id) return AttendanceErrors.Unauthorized;
        if (!await db.Users.AnyAsync(u => u.Id == id && u.EmailConfirmed && db.UserRoles.Any(ur => ur.UserId == id && db.Roles.Any(r => r.Id == ur.RoleId && (r.NormalizedName == "DOCTOR" || r.NormalizedName == "RECEPTIONIST"))), ct)) return AttendanceErrors.Forbidden;
        return id;
    }
    private IQueryable<BookingSession> Visible(Guid actor) => db.BookingSessions.AsNoTracking().Where(s =>
        db.DoctorPracticeLocations.Any(p => p.Id == s.LocationId && p.DoctorProfileId == s.DoctorProfileId && p.UserId == s.UserId && p.IsActive && p.Status == PracticeLocationStatus.Approved) &&
        access.Doctors.Any(d => d.Id == s.DoctorProfileId && d.UserId == s.UserId) &&
        (s.OperationalBranchId == null || db.ClinicBranches.Any(b => b.Id == s.OperationalBranchId && b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive))) &&
        (s.UserId == actor || (db.UserRoles.Any(ur => ur.UserId == actor && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "RECEPTIONIST")) &&
            db.ReceptionistAssignments.Any(a => a.UserId == actor && a.IsActive && ((s.OperationalBranchId == null && a.LocationId == s.LocationId && a.BranchId == null) ||
                (s.OperationalBranchId != null && a.BranchId == s.OperationalBranchId && a.LocationId == null))))));
    public async Task<Result<IReadOnlyList<AttendanceDoctor>>> DoctorsAsync(CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<AttendanceDoctor>>.Failure(actor.Errors);
        var sessions = Visible(actor.Value);
        return Result<IReadOnlyList<AttendanceDoctor>>.Success(await access.Doctors.Where(d => sessions.Any(s => s.DoctorProfileId == d.Id)).OrderBy(d => d.ProfessionalName).ThenBy(d => d.Id).Select(d => new AttendanceDoctor(d.Id, d.ProfessionalName)).ToArrayAsync(ct));
    }
    public async Task<Result<IReadOnlyList<AttendancePlace>>> PlacesAsync(Guid doctor, CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor.IsError) return Result<IReadOnlyList<AttendancePlace>>.Failure(actor.Errors);
        var sessions = Visible(actor.Value).Where(s => s.DoctorProfileId == doctor); if (!await sessions.AnyAsync(ct)) return AttendanceErrors.NotFound;
        var rows = await (from s in sessions
                          join p in db.DoctorPracticeLocations on s.LocationId equals p.Id
                          select new
                          {
                              p.Id,
                              s.OperationalBranchId,
                              Name = s.OperationalBranchId == null ? p.Name : db.ClinicBranches.Where(b => b.Id == s.OperationalBranchId).Select(b => b.Name).Single(),
                              Address = s.OperationalBranchId == null ? p.Address : db.ClinicBranches.Where(b => b.Id == s.OperationalBranchId).Select(b => b.Address).Single()
                          })
            .Distinct().OrderBy(p => p.Id).ThenBy(p => p.OperationalBranchId).Select(p => new AttendancePlace(p.Id, p.OperationalBranchId, p.Name, p.Address)).ToArrayAsync(ct);
        return Result<IReadOnlyList<AttendancePlace>>.Success(rows);
    }
    public async Task<Result<AttendancePage<AttendanceSession>>> SessionsAsync(Guid doctor, Guid location, DateOnly from, DateOnly until, int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor.IsError) return Result<AttendancePage<AttendanceSession>>.Failure(actor.Errors);
        var scope = Visible(actor.Value).Where(s => s.DoctorProfileId == doctor && s.LocationId == location);
        if (!await scope.AnyAsync(ct)) return AttendanceErrors.NotFound;
        var query = scope.Where(s => s.LocalDate >= from && s.LocalDate <= until); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(s => s.StartsAtUtc).ThenBy(s => s.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new AttendancePage<AttendanceSession>(rows.Select(Session).ToArray(), count, page, size);
    }
    public async Task<Result<AttendanceQueue>> QueueAsync(Guid doctor, Guid session, int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor.IsError) return Result<AttendanceQueue>.Failure(actor.Errors);
        var scope = await Visible(actor.Value).SingleOrDefaultAsync(s => s.Id == session && s.DoctorProfileId == doctor, ct); if (scope is null) return AttendanceErrors.NotFound;
        var query = db.Appointments.AsNoTracking().Where(a => a.SessionId == session && a.DoctorProfileId == doctor); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(a => a.QueueNumber == null).ThenBy(a => a.QueueNumber).ThenBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new AttendanceQueue(Session(scope), new(rows.Select(AppointmentDto).ToArray(), count, page, size));
    }
    public async Task<Result<AttendancePage<AttendanceHistory>>> HistoryAsync(Guid doctor, Guid session, Guid appointment, int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor.IsError) return Result<AttendancePage<AttendanceHistory>>.Failure(actor.Errors);
        if (!await Visible(actor.Value).AnyAsync(s => s.Id == session && s.DoctorProfileId == doctor, ct) || !await db.Appointments.AnyAsync(a => a.Id == appointment && a.SessionId == session && a.DoctorProfileId == doctor, ct)) return AttendanceErrors.NotFound;
        var query = db.AttendanceRecords.AsNoTracking().Where(r => r.AppointmentId == appointment && r.SessionId == session && r.DoctorProfileId == doctor); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.Revision).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new AttendancePage<AttendanceHistory>(rows.Select(r => new AttendanceHistory(r.Id, r.Revision, r.ActorUserId, r.RecordedAtUtc, r.Action.ToString(), r.Reason,
            JsonSerializer.Deserialize<AttendanceFacts>(r.BeforeFactsJson) ?? throw new InvalidOperationException("Invalid attendance history."),
            JsonSerializer.Deserialize<AttendanceFacts>(r.AfterFactsJson) ?? throw new InvalidOperationException("Invalid attendance history."))).ToArray(), count, page, size);
    }
    public async Task<Result<AttendanceWriteResult>> WriteAsync(Guid doctor, Guid session, Guid appointment, AttendanceInput input, CancellationToken ct)
    {
        access.Fresh(); var actor = await ActorAsync(ct); if (actor.IsError) return Result<AttendanceWriteResult>.Failure(actor.Errors);
        var snapshot = await Visible(actor.Value).SingleOrDefaultAsync(s => s.Id == session && s.DoctorProfileId == doctor, ct); if (snapshot is null) return AttendanceErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{doctor:N}", ct) || !await access.LockAsync($"ClinIQ.BookingSession.{session:N}", ct) ||
            !await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.OperationalBranchId, snapshot.OperationalBranchId == null ? snapshot.LocationId : null), ct)) return AttendanceErrors.Busy;
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH(HOLDLOCK) WHERE Id={actor.Value}").ToArrayAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH(HOLDLOCK) WHERE UserId={actor.Value}").ToArrayAsync(ct);
        var liveActor = await ActorAsync(ct); if (liveActor.IsError) return Result<AttendanceWriteResult>.Failure(liveActor.Errors);
        if (!await Visible(actor.Value).AnyAsync(s => s.Id == session && s.DoctorProfileId == doctor, ct)) return AttendanceErrors.NotFound;
        var responsible = snapshot.UserId == actor.Value;
        if (input.Action == AttendanceAction.LateCheckIn && !responsible) return AttendanceErrors.DoctorRequired;
        var row = await db.Appointments.SingleOrDefaultAsync(a => a.Id == appointment && a.SessionId == session && a.DoctorProfileId == doctor, ct); if (row is null) return AttendanceErrors.NotFound;
        if (row.PatientId != input.ConfirmedPatientId) return AttendanceErrors.PatientConfirmation;
        input = input with { ArrivedAtUtc = input.ArrivedAtUtc?.ToUniversalTime(), CompletedAtUtc = input.CompletedAtUtc?.ToUniversalTime() };
        if (!row.IsAttendanceReplay(input) && !WorkforceAccess.Matches(row.Version, input.Version)) return AttendanceErrors.Stale;
        var sourceVersion = row.Version.ToArray();
        var liveSession = await db.BookingSessions.SingleAsync(s => s.Id == session, ct); var now = clock.GetUtcNow();
        var changed = row.RecordAttendance(input, actor.Value, responsible, liveSession.Status == BookingSessionStatus.Cancelled, now, row.QueueNumber ?? checked(liveSession.LastQueueNumber + 1));
        if (changed.IsError) return Result<AttendanceWriteResult>.Failure(changed.Errors);
        if (!changed.Value.Changed) return new AttendanceWriteResult(AppointmentDto(row), false);
        if (changed.Value.Before.QueueNumber == null && row.QueueNumber.HasValue && liveSession.IssueQueueNumber() != row.QueueNumber) throw new InvalidOperationException("Queue sequence mismatch.");
        var record = AttendanceRecord.Create(row, actor.Value, now, input, changed.Value,
            JsonSerializer.Serialize(changed.Value.Before), JsonSerializer.Serialize(changed.Value.After), sourceVersion);
        db.AttendanceRecords.Add(record);
        if (changed.Value.After.Status == AppointmentStatus.NoShow && changed.Value.Before.Status != AppointmentStatus.NoShow)
        {
            var feedback = ClinIQ.Domain.AttendanceFeedback.AttendanceFeedbackRequest.Create(row, record);
            db.AttendanceFeedbackRequests.Add(feedback);
            db.FeedbackNotices.Add(ClinIQ.Domain.Notifications.FeedbackNotice.Create(feedback, ClinIQ.Domain.Notifications.FeedbackNoticeKind.InitialPush));
            db.FeedbackNotices.Add(ClinIQ.Domain.Notifications.FeedbackNotice.Create(feedback, ClinIQ.Domain.Notifications.FeedbackNoticeKind.EmailReminder));
        }
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new AttendanceWriteResult(AppointmentDto(row), true); }
        catch (DbUpdateConcurrencyException) { return AttendanceErrors.Stale; }
    }
    private static AttendanceSession Session(BookingSession s) => new(s.Id, s.DoctorProfileId, s.LocationId, s.OperationalBranchId, s.LocalDate, s.StartsAtUtc, s.EndsAtUtc, s.TimeZoneId, s.Status.ToString(), s.MaxCapacity, s.AllocatedCount, s.LastQueueNumber);
    private static AttendanceAppointment AppointmentDto(Appointment a) => new(a.Id, a.PatientId, a.ContactName, a.ContactPhone, a.Fee, a.Currency, a.Attendance, Convert.ToBase64String(a.Version));
}
