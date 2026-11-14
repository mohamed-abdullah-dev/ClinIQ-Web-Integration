using System.Text.Json;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Application.Features.AttendanceFeedback.Commands;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.AttendanceFeedback;

internal sealed class AttendanceFeedbackService(AppDbContext db, ICurrentUser current, IHttpContextAccessor http,
    WorkforceAccess access, TimeProvider clock) : IAttendanceFeedbackService
{
    private async Task<Result<Guid>> ActorAsync(bool admin, CancellationToken ct)
    {
        if (current.UserId is not Guid id) return FeedbackErrors.Unauthorized;
        var client = admin ? "Web" : "Mobile";
        if (http.HttpContext?.User.FindFirst("client")?.Value != client) return FeedbackErrors.Forbidden;
        var role = admin ? "ADMIN" : "PATIENT";
        if (!await db.Users.AnyAsync(u => u.Id == id && u.EmailConfirmed &&
            db.UserRoles.Any(ur => ur.UserId == id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == role)), ct) ||
            (!admin && !await db.PatientProfiles.AnyAsync(p => p.UserId == id, ct))) return FeedbackErrors.Forbidden;
        return id;
    }
    private async Task HoldActorAsync(Guid id, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH(HOLDLOCK) WHERE Id={id}").ToArrayAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH(HOLDLOCK) WHERE UserId={id}").ToArrayAsync(ct);
    }
    private IQueryable<AttendanceFeedbackRequest> Owned(Guid actor) => db.AttendanceFeedbackRequests.Where(r => r.PatientUserId == actor);
    private FeedbackDto Dto(AttendanceFeedbackRequest r, Appointment a)
    {
        var applicable = r.IsApplicable(a); var now = clock.GetUtcNow();
        return new(r.Id, r.AppointmentId, r.SourceAttendanceRecordId, r.SourceRevision, r.CreatedAtUtc, r.ExpiresAtUtc,
            r.Outcome, r.ResponseRevision, r.RespondedAtUtc, r.ReviewState, r.Finding, r.ReviewedAtUtc, applicable,
            applicable && !r.IsFinal && now >= r.CreatedAtUtc && now < r.ExpiresAtUtc, Convert.ToBase64String(r.Version));
    }
    private static FeedbackResponseDto Response(AttendanceFeedbackResponse r) => new(r.Id, r.SubmissionId, r.Revision, r.ActorUserId, r.Outcome, r.SubmittedAtUtc, r.IsEdit);
    private FeedbackReviewDetail Detail(AttendanceFeedbackRequest r, Appointment a) => new(Dto(r, a), r.PatientUserId,
        a.Selection, a.Attendance, Convert.ToBase64String(a.Version), r.ReviewedByUserId, r.ReviewReason, r.ReviewedResponseRevision,
        r.ReviewedAppointmentVersion is null ? null : Convert.ToBase64String(r.ReviewedAppointmentVersion));
    private async Task<AttendancePage<FeedbackDto>> PageAsync(IQueryable<AttendanceFeedbackRequest> query, int page, int size, CancellationToken ct)
    {
        var count = await query.CountAsync(ct);
        // One joined SQL read keeps applicability consistent with the returned request snapshot.
        var rows = await (from r in query.AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                          orderby r.CreatedAtUtc descending, r.Id select new { Request = r, Appointment = a })
            .Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new(rows.Select(x => Dto(x.Request, x.Appointment)).ToArray(), count, page, size);
    }
    public async Task<Result<AttendancePage<FeedbackDto>>> ListAsync(int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<AttendancePage<FeedbackDto>>.Failure(actor.Errors);
        return await PageAsync(Owned(actor.Value), page, size, ct);
    }
    public async Task<Result<FeedbackDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<FeedbackDto>.Failure(actor.Errors);
        var row = await (from r in Owned(actor.Value).AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                         where r.Id == id select new { Request = r, Appointment = a }).SingleOrDefaultAsync(ct);
        return row is null ? FeedbackErrors.NotFound : Dto(row.Request, row.Appointment);
    }
    public async Task<Result<FeedbackAnswerReceipt>> AnswerAsync(Guid id, FeedbackAnswerInput input, bool edit, CancellationToken ct)
    {
        access.Fresh();
        if (input is null || !(await new FeedbackAnswerInputValidator().ValidateAsync(input, ct)).IsValid) return FeedbackErrors.Outcome;
        var actor = await ActorAsync(false, ct); if (actor.IsError) return Result<FeedbackAnswerReceipt>.Failure(actor.Errors);
        var snapshot = await (from r in Owned(actor.Value).AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                              where r.Id == id select new { a.DoctorProfileId, a.SessionId }).SingleOrDefaultAsync(ct);
        if (snapshot is null) return FeedbackErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{snapshot.DoctorProfileId:N}", ct) ||
            !await access.LockAsync($"ClinIQ.BookingSession.{snapshot.SessionId:N}", ct)) return FeedbackErrors.Busy;
        await HoldActorAsync(actor.Value, ct);
        var liveActor = await ActorAsync(false, ct); if (liveActor.IsError) return Result<FeedbackAnswerReceipt>.Failure(liveActor.Errors);
        var request = await Owned(actor.Value).SingleOrDefaultAsync(r => r.Id == id, ct); if (request is null) return FeedbackErrors.NotFound;
        var appointment = await db.Appointments.SingleAsync(a => a.Id == request.AppointmentId, ct);
        var previous = await db.AttendanceFeedbackResponses.AsNoTracking().SingleOrDefaultAsync(r => r.FeedbackRequestId == id && r.SubmissionId == input.SubmissionId, ct);
        if (previous is not null)
        {
            if (previous.Outcome != input.Outcome || previous.IsEdit != edit || !WorkforceAccess.Matches(previous.PreviousVersion, input.Version)) return FeedbackErrors.ReplayMismatch;
            return new FeedbackAnswerReceipt(Dto(request, appointment), Response(previous), true);
        }
        if (!WorkforceAccess.Matches(request.Version, input.Version)) return FeedbackErrors.Stale;
        var sourceVersion = request.Version.ToArray();
        var changed = request.Respond(input.Outcome, edit, request.IsApplicable(appointment), clock.GetUtcNow());
        if (changed.IsError) return Result<FeedbackAnswerReceipt>.Failure(changed.Errors);
        var answer = AttendanceFeedbackResponse.Create(request, actor.Value, input.SubmissionId, edit, sourceVersion);
        db.AttendanceFeedbackResponses.Add(answer);
        try
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new FeedbackAnswerReceipt(Dto(request, appointment), Response(answer), false);
        }
        catch (DbUpdateConcurrencyException) { return FeedbackErrors.Stale; }
    }
    public async Task<Result<AttendancePage<FeedbackDto>>> ReviewsAsync(AttendanceReviewState state, int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<AttendancePage<FeedbackDto>>.Failure(actor.Errors);
        return await PageAsync(db.AttendanceFeedbackRequests.Where(r => r.ReviewState == state), page, size, ct);
    }
    public async Task<Result<FeedbackReviewDetail>> ReviewAsync(Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<FeedbackReviewDetail>.Failure(actor.Errors);
        var row = await (from r in db.AttendanceFeedbackRequests.AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                         where r.Id == id && r.ReviewState != null select new { Request = r, Appointment = a }).SingleOrDefaultAsync(ct);
        return row is null ? FeedbackErrors.NotFound : Detail(row.Request, row.Appointment);
    }
    public async Task<Result<AttendancePage<FeedbackResponseDto>>> ResponsesAsync(Guid id, int page, int size, bool admin, CancellationToken ct)
    {
        var actor = await ActorAsync(admin, ct); if (actor.IsError) return Result<AttendancePage<FeedbackResponseDto>>.Failure(actor.Errors);
        if (!await db.AttendanceFeedbackRequests.AnyAsync(r => r.Id == id && (admin ? r.ReviewState != null : r.PatientUserId == actor.Value), ct)) return FeedbackErrors.NotFound;
        var query = db.AttendanceFeedbackResponses.AsNoTracking().Where(r => r.FeedbackRequestId == id); var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.Revision).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new AttendancePage<FeedbackResponseDto>(rows.Select(Response).ToArray(), total, page, size);
    }
    public async Task<Result<AttendancePage<AttendanceHistory>>> StaffHistoryAsync(Guid id, int page, int size, CancellationToken ct)
    {
        var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<AttendancePage<AttendanceHistory>>.Failure(actor.Errors);
        var request = await db.AttendanceFeedbackRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.ReviewState != null, ct);
        if (request is null) return FeedbackErrors.NotFound;
        var query = db.AttendanceRecords.AsNoTracking().Where(r => r.AppointmentId == request.AppointmentId); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.Revision).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        return new AttendancePage<AttendanceHistory>(rows.Select(r => new AttendanceHistory(r.Id, r.Revision, r.ActorUserId, r.RecordedAtUtc, r.Action.ToString(), r.Reason,
            JsonSerializer.Deserialize<AttendanceFacts>(r.BeforeFactsJson) ?? throw new InvalidOperationException("Invalid attendance history."),
            JsonSerializer.Deserialize<AttendanceFacts>(r.AfterFactsJson) ?? throw new InvalidOperationException("Invalid attendance history."))).ToArray(), count, page, size);
    }
    public async Task<Result<FeedbackReviewDetail>> FinalizeAsync(Guid id, FeedbackReviewInput input, CancellationToken ct)
    {
        access.Fresh();
        if (input is null || !(await new FeedbackReviewInputValidator().ValidateAsync(input, ct)).IsValid) return FeedbackErrors.Finding;
        var actor = await ActorAsync(true, ct); if (actor.IsError) return Result<FeedbackReviewDetail>.Failure(actor.Errors);
        var snapshot = await (from r in db.AttendanceFeedbackRequests.AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                              join s in db.BookingSessions.AsNoTracking() on a.SessionId equals s.Id
                              where r.Id == id && r.ReviewState != null select s).SingleOrDefaultAsync(ct);
        if (snapshot is null) return FeedbackErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await access.LockAsync($"ClinIQ.DoctorSchedule.{snapshot.DoctorProfileId:N}", ct) ||
            !await access.LockAsync($"ClinIQ.BookingSession.{snapshot.Id:N}", ct) ||
            !await access.LockAsync(WorkforceAccess.ScopeLock(snapshot.OperationalBranchId, snapshot.OperationalBranchId == null ? snapshot.LocationId : null), ct)) return FeedbackErrors.Busy;
        await HoldActorAsync(actor.Value, ct);
        var liveActor = await ActorAsync(true, ct); if (liveActor.IsError) return Result<FeedbackReviewDetail>.Failure(liveActor.Errors);
        var request = await db.AttendanceFeedbackRequests.SingleAsync(r => r.Id == id, ct);
        var appointment = await db.Appointments.SingleAsync(a => a.Id == request.AppointmentId, ct);
        if (!WorkforceAccess.Matches(request.Version, input.Version) || !WorkforceAccess.Matches(appointment.Version, input.AppointmentVersion)) return FeedbackErrors.Stale;
        if (request.IsFinal) return FeedbackErrors.Final;
        if (request.ReviewState != AttendanceReviewState.Open) return FeedbackErrors.NoReview;
        var sourceVersion = appointment.Version.ToArray(); var now = clock.GetUtcNow();
        if (input.State == AttendanceReviewState.Resolved)
        {
            // Closing a case is allowed after closure; changing attendance still obeys the live operational scope.
            var session = await db.BookingSessions.SingleAsync(s => s.Id == snapshot.Id, ct);
            var target = input.Finding switch
            {
                PatientAttendanceOutcome.DidNotAttend => AppointmentStatus.NoShow,
                PatientAttendanceOutcome.AttendedAndSeen => AppointmentStatus.Completed,
                PatientAttendanceOutcome.AttendedNotSeen => AppointmentStatus.AttendedNotSeen,
                _ => throw new InvalidOperationException("The validated finding is missing.")
            };
            var correction = new AttendanceInput(appointment.PatientId, AttendanceAction.Correct, input.AppointmentVersion,
                input.ArrivedAtUtc?.ToUniversalTime(), input.CompletedAtUtc?.ToUniversalTime(), target, input.Reason);
            var change = appointment.RecordAttendance(correction, actor.Value, false, session.Status == BookingSessionStatus.Cancelled,
                now, appointment.QueueNumber ?? checked(session.LastQueueNumber + 1));
            if (change.IsError) return Result<FeedbackReviewDetail>.Failure(change.Errors);
            if (change.Value.Changed)
            {
                if (!await CorrectionScopeAsync(snapshot, ct)) return FeedbackErrors.Closed;
                if (change.Value.Before.QueueNumber == null && appointment.QueueNumber.HasValue && session.IssueQueueNumber() != appointment.QueueNumber) throw new InvalidOperationException("Queue sequence mismatch.");
                var record = AttendanceRecord.Create(appointment, actor.Value, now, correction, change.Value,
                    JsonSerializer.Serialize(change.Value.Before), JsonSerializer.Serialize(change.Value.After), sourceVersion);
                db.AttendanceRecords.Add(record);
                // Administrative correction is not a fresh staff NoShow request or a new response window.
            }
        }
        var finalized = request.FinalizeReview(input.State, input.Finding, input.Reason, actor.Value, sourceVersion, now);
        if (finalized.IsError) return Result<FeedbackReviewDetail>.Failure(finalized.Errors);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Detail(request, appointment); }
        catch (DbUpdateConcurrencyException) { return FeedbackErrors.Stale; }
    }
    private Task<bool> CorrectionScopeAsync(BookingSession session, CancellationToken ct) => db.DoctorPracticeLocations.AnyAsync(p =>
        p.Id == session.LocationId && p.DoctorProfileId == session.DoctorProfileId && p.IsActive && p.Status == PracticeLocationStatus.Approved &&
        access.Doctors.Any(d => d.Id == session.DoctorProfileId) && (session.OperationalBranchId == null ||
            db.ClinicBranches.Any(b => b.Id == session.OperationalBranchId && b.IsActive && db.Clinics.Any(c => c.Id == b.ClinicId && c.IsActive))), ct);
}
