using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.VisitReviews;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace ClinIQ.Infrastructure.Data.Configurations;
public sealed class VisitReviewConfiguration:IEntityTypeConfiguration<VisitReview>
{
    public void Configure(EntityTypeBuilder<VisitReview> b)
    {
        b.ToTable("VisitReviews",t=>t.HasCheckConstraint("CK_VisitReviews_Facts","[Stars] BETWEEN 1 AND 5 AND [Revision]>0 AND (([Hidden]=1 AND [ModerationCategory] IS NOT NULL AND [ModerationCategory] IN (1,2,3) AND [ModeratedByUserId] IS NOT NULL AND [ModeratedAtUtc] IS NOT NULL AND [ModerationReason] IS NOT NULL AND LEN([ModerationReason])>0) OR ([Hidden]=0 AND [ModerationCategory] IS NULL))"));
        b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.Version).IsRowVersion();
        b.HasIndex(x=>x.AppointmentId).IsUnique();b.HasIndex(x=>new{x.DoctorProfileId,x.Hidden,x.CreatedAtUtc,x.Id});
        b.HasOne<Appointment>().WithMany().HasForeignKey(x=>new{x.AppointmentId,x.PatientId,x.DoctorProfileId,x.LocationId,x.PatientUserId}).HasPrincipalKey(x=>new{x.Id,x.PatientId,x.DoctorProfileId,x.LocationId,x.PatientUserId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x=>x.ModeratedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x=>x.LocationName).HasMaxLength(200).IsRequired();b.Property(x=>x.Text).HasMaxLength(2000);b.Property(x=>x.ModerationReason).HasMaxLength(2000);
    }
}
public sealed class VisitReviewAuditConfiguration:IEntityTypeConfiguration<VisitReviewAudit>
{
    public void Configure(EntityTypeBuilder<VisitReviewAudit> b)
    {
        b.ToTable("VisitReviewAudits",t=>{t.HasTrigger("TR_VisitReviewAudits_Immutable");t.UseSqlOutputClause(false);t.HasCheckConstraint("CK_VisitReviewAudits_Facts","[Revision]>0 AND [Action] IN ('Create','Edit','Hide','Restore') AND ISJSON([BeforeJson])=1 AND ISJSON([AfterJson])=1");});
        b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.HasOne<VisitReview>().WithMany().HasForeignKey(x=>x.ReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x=>x.ActorUserId).OnDelete(DeleteBehavior.Restrict);b.HasIndex(x=>new{x.ReviewId,x.Revision}).IsUnique();
        b.Property(x=>x.Action).HasMaxLength(10).IsUnicode(false);b.Property(x=>x.BeforeJson).HasMaxLength(32000);b.Property(x=>x.AfterJson).HasMaxLength(32000);b.Property(x=>x.AppointmentVersion).HasColumnType("binary(8)");
    }
}
