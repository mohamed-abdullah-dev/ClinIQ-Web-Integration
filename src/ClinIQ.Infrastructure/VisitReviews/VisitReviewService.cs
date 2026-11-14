using System.Text.Json;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Application.Features.VisitReviews.Commands;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.VisitReviews;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
namespace ClinIQ.Infrastructure.VisitReviews;
internal sealed class VisitReviewService(AppDbContext db,ICurrentUser current,IHttpContextAccessor http,WorkforceAccess access,TimeProvider clock):IVisitReviewService
{
    private async Task<Result<Guid>> Actor(bool admin,CancellationToken ct)
    {
        if(current.UserId is not Guid id)return VisitReviewErrors.Unauthorized;
        var role=admin?"ADMIN":"PATIENT";var client=admin?"Web":"Mobile";
        if(http.HttpContext?.User.FindFirst("client")?.Value!=client || !await db.Users.AnyAsync(u=>u.Id==id&&u.EmailConfirmed&&db.UserRoles.Any(ur=>ur.UserId==id&&db.Roles.Any(r=>r.Id==ur.RoleId&&r.NormalizedName==role)),ct))return VisitReviewErrors.Forbidden;
        return id;
    }
    private async Task HoldActor(Guid id,CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM AspNetUsers WITH(HOLDLOCK) WHERE Id={id}").ToArrayAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AspNetUserRoles WITH(HOLDLOCK) WHERE UserId={id}").ToArrayAsync(ct);
    }
    private async Task<bool> Lock(Appointment a,CancellationToken ct)=>await access.LockAsync($"ClinIQ.DoctorSchedule.{a.DoctorProfileId:N}",ct)&&await access.LockAsync($"ClinIQ.BookingSession.{a.SessionId:N}",ct)&&await access.LockAsync($"ClinIQ.VisitReview.{a.Id:N}",ct);
    private static bool Eligible(Appointment a)=>a.Status==AppointmentStatus.Completed&&a.CompletedAtUtc.HasValue;
    private static VisitReviewDto Dto(VisitReview r,Appointment a)=>new(r.Id,r.AppointmentId,r.DoctorProfileId,r.LocationId,r.Stars,r.Text,r.Hidden,Eligible(a),r.CreatedAtUtc,r.EditedAtUtc,a.CompletedAtUtc?.AddDays(30),Convert.ToBase64String(r.Version));
    private static string Snapshot(VisitReview r)=>JsonSerializer.Serialize(new{r.Stars,r.Text,r.Hidden,r.ModerationCategory,r.ModerationReason,r.ModeratedByUserId,r.ModeratedAtUtc});
    private void Audit(VisitReview r,Appointment a,Guid actor,string action,string before)=>db.VisitReviewAudits.Add(VisitReviewAudit.Create(r,actor,action,before,Snapshot(r),a.Version,clock.GetUtcNow()));
    public async Task<Result<VisitReviewWriteResult>> WriteAsync(Guid appointment,WriteVisitReviewInput input,bool edit,CancellationToken ct)
    {
        access.Fresh();if(input is null||!(await new WriteReviewInputValidator().ValidateAsync(input,ct)).IsValid)return VisitReviewErrors.Input;
        var actor=await Actor(false,ct);if(actor.IsError)return Result<VisitReviewWriteResult>.Failure(actor.Errors);
        var source=await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a=>a.Id==appointment&&a.PatientUserId==actor.Value,ct);if(source is null)return VisitReviewErrors.NotFound;
        await using var tx=await db.Database.BeginTransactionAsync(ct);if(!await Lock(source,ct))return VisitReviewErrors.Busy;
        await HoldActor(actor.Value,ct);var live=await Actor(false,ct);if(live.IsError)return Result<VisitReviewWriteResult>.Failure(live.Errors);
        var a=await db.Appointments.SingleAsync(x=>x.Id==appointment&&x.PatientUserId==actor.Value,ct);
        var row=await db.VisitReviews.SingleOrDefaultAsync(r=>r.AppointmentId==appointment,ct);
        if(!edit)
        {
            if(row is not null)return row.Stars==input.Stars&&row.Text==VisitReview.Normalize(input.Text)?new VisitReviewWriteResult(Dto(row,a),true):VisitReviewErrors.Exists;
            var created=VisitReview.Create(a,input.Stars,input.Text,clock.GetUtcNow());if(created.IsError)return Result<VisitReviewWriteResult>.Failure(created.Errors);
            row=created.Value;db.VisitReviews.Add(row);Audit(row,a,actor.Value,"Create","{}");
        }
        else
        {
            if(row is null)return VisitReviewErrors.NotFound;
            if(!WorkforceAccess.Matches(row.Version,input.Version??""))return VisitReviewErrors.Stale;
            var before=Snapshot(row);var changed=row.Edit(a,input.Stars,input.Text,clock.GetUtcNow());if(changed.IsError)return Result<VisitReviewWriteResult>.Failure(changed.Errors);
            if(!changed.Value)return new VisitReviewWriteResult(Dto(row,a),true);
            Audit(row,a,actor.Value,"Edit",before);
        }
        try{await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new VisitReviewWriteResult(Dto(row,a),false);}
        catch(DbUpdateConcurrencyException){return VisitReviewErrors.Stale;}
    }
    public async Task<Result<VisitReviewDto>> OwnAsync(Guid appointment,CancellationToken ct)
    {
        var actor=await Actor(false,ct);if(actor.IsError)return Result<VisitReviewDto>.Failure(actor.Errors);
        var row=await(from r in db.VisitReviews.AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id where r.AppointmentId==appointment&&r.PatientUserId==actor.Value select new{Review=r,Appointment=a}).SingleOrDefaultAsync(ct);
        return row is null?VisitReviewErrors.NotFound:Dto(row.Review,row.Appointment);
    }
    public async Task<Result<VisitReviewPage<PublicVisitReview>>> PublicAsync(Guid doctor,int page,int size,CancellationToken ct)
    {
        if(!await access.Doctors.AnyAsync(d=>d.Id==doctor,ct))return VisitReviewErrors.NotFound;
        var q=db.VisitReviews.AsNoTracking().Where(r=>r.DoctorProfileId==doctor&&!r.Hidden&&db.Appointments.Any(a=>a.Id==r.AppointmentId&&a.Status==AppointmentStatus.Completed&&a.CompletedAtUtc!=null));
        var count=await q.CountAsync(ct);var rows=await q.OrderByDescending(r=>r.CreatedAtUtc).ThenBy(r=>r.Id).Skip((page-1)*size).Take(size).Select(r=>new PublicVisitReview(r.Id,r.LocationId,r.LocationName,r.Stars,r.Text,r.CreatedAtUtc,r.EditedAtUtc)).ToArrayAsync(ct);
        return new VisitReviewPage<PublicVisitReview>(rows,count,page,size);
    }
    public async Task<Result<VisitReviewPage<VisitReviewDto>>> AdminAsync(int page,int size,CancellationToken ct)
    {
        var actor=await Actor(true,ct);if(actor.IsError)return Result<VisitReviewPage<VisitReviewDto>>.Failure(actor.Errors);
        var q=from r in db.VisitReviews.AsNoTracking() join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id orderby r.CreatedAtUtc descending,r.Id select new{Review=r,Appointment=a};
        var total=await q.CountAsync(ct);var rows=await q.Skip((page-1)*size).Take(size).ToArrayAsync(ct);return new VisitReviewPage<VisitReviewDto>(rows.Select(x=>Dto(x.Review,x.Appointment)).ToArray(),total,page,size);
    }
    public async Task<Result<VisitReviewDto>> ModerateAsync(Guid id,ModerateVisitReviewInput input,CancellationToken ct)
    {
        access.Fresh();if(input is null||!(await new ModerateReviewInputValidator().ValidateAsync(input,ct)).IsValid)return VisitReviewErrors.Moderation;
        var actor=await Actor(true,ct);if(actor.IsError)return Result<VisitReviewDto>.Failure(actor.Errors);
        var source=await(from r in db.VisitReviews.AsNoTracking() join booking in db.Appointments.AsNoTracking() on r.AppointmentId equals booking.Id where r.Id==id select booking).SingleOrDefaultAsync(ct);if(source is null)return VisitReviewErrors.NotFound;
        await using var tx=await db.Database.BeginTransactionAsync(ct);if(!await Lock(source,ct))return VisitReviewErrors.Busy;
        await HoldActor(actor.Value,ct);var live=await Actor(true,ct);if(live.IsError)return Result<VisitReviewDto>.Failure(live.Errors);
        var row=await db.VisitReviews.SingleAsync(r=>r.Id==id,ct);var a=await db.Appointments.AsNoTracking().SingleAsync(a=>a.Id==source.Id,ct);
        if(!WorkforceAccess.Matches(row.Version,input.Version))return VisitReviewErrors.Stale;
        var before=Snapshot(row);var changed=row.Moderate(input.Hide,input.Category,input.Reason,actor.Value,clock.GetUtcNow());if(changed.IsError)return Result<VisitReviewDto>.Failure(changed.Errors);
        if(!changed.Value)return Dto(row,a);Audit(row,a,actor.Value,input.Hide?"Hide":"Restore",before);
        try{await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Dto(row,a);}catch(DbUpdateConcurrencyException){return VisitReviewErrors.Stale;}
    }
    public async Task<Result<VisitReviewPage<VisitReviewAuditDto>>> HistoryAsync(Guid id,int page,int size,CancellationToken ct)
    {
        var actor=await Actor(true,ct);if(actor.IsError)return Result<VisitReviewPage<VisitReviewAuditDto>>.Failure(actor.Errors);
        if(!await db.VisitReviews.AnyAsync(r=>r.Id==id,ct))return VisitReviewErrors.NotFound;
        var q=db.VisitReviewAudits.AsNoTracking().Where(r=>r.ReviewId==id);var total=await q.CountAsync(ct);
        var rows=await q.OrderBy(r=>r.Revision).Skip((page-1)*size).Take(size).Select(r=>new VisitReviewAuditDto(r.Revision,r.ActorUserId,r.Action,r.BeforeJson,r.AfterJson,r.RecordedAtUtc)).ToArrayAsync(ct);
        return new VisitReviewPage<VisitReviewAuditDto>(rows,total,page,size);
    }
}
