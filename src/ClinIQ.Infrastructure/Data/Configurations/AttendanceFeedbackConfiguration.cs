using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Patients;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class AttendanceFeedbackRequestConfiguration : IEntityTypeConfiguration<AttendanceFeedbackRequest>
{
    public void Configure(EntityTypeBuilder<AttendanceFeedbackRequest> b)
    {
        b.ToTable("AttendanceFeedbackRequests", t =>
        {
            t.HasTrigger("TR_AttendanceFeedbackRequests_Protect"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_AttendanceFeedbackRequests_Window", "[SourceRevision]>0 AND [ExpiresAtUtc]=DATEADD(day,7,[CreatedAtUtc])");
            t.HasCheckConstraint("CK_AttendanceFeedbackRequests_Answer", "([ResponseRevision]=0 AND [Outcome] IS NULL AND [RespondedAtUtc] IS NULL AND [ReviewState] IS NULL) OR ([ResponseRevision]>0 AND [Outcome] IS NOT NULL AND [Outcome] IN (1,2,3) AND [RespondedAtUtc] IS NOT NULL AND [RespondedAtUtc]>=[CreatedAtUtc] AND [RespondedAtUtc]<[ExpiresAtUtc] AND ([ReviewState] IS NOT NULL OR [Outcome]=3))");
            t.HasCheckConstraint("CK_AttendanceFeedbackRequests_Review", "(([ReviewState] IS NULL OR [ReviewState]=1) AND [Finding] IS NULL AND [ReviewedByUserId] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewReason] IS NULL AND [ReviewedResponseRevision] IS NULL AND [ReviewedAppointmentVersion] IS NULL) OR ([ReviewState] IS NOT NULL AND [ReviewState] IN (2,3) AND [ReviewedByUserId] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedAtUtc]>=[RespondedAtUtc] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason])>0 AND [ReviewedResponseRevision] IS NOT NULL AND [ReviewedResponseRevision]=[ResponseRevision] AND [ReviewedAppointmentVersion] IS NOT NULL AND (([ReviewState]=2 AND [Finding] IS NOT NULL AND [Finding] IN (1,2,3)) OR ([ReviewState]=3 AND [Finding] IS NULL)))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion(); b.Ignore(x => x.IsFinal);
        b.HasAlternateKey(x => new { x.Id, x.PatientUserId });
        b.HasOne<AttendanceRecord>().WithMany().HasForeignKey(x => new { x.SourceAttendanceRecordId, x.AppointmentId, x.SourceRevision })
            .HasPrincipalKey(x => new { x.Id, x.AppointmentId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => new { x.AppointmentId, x.PatientId })
            .HasPrincipalKey(x => new { x.Id, x.PatientId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => new { x.PatientId, x.PatientUserId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SourceAttendanceRecordId).IsUnique(); b.HasIndex(x => new { x.PatientUserId, x.CreatedAtUtc, x.Id });
        b.HasIndex(x => new { x.ReviewState, x.CreatedAtUtc, x.Id });
        b.Property(x => x.ReviewReason).HasMaxLength(2000); b.Property(x => x.ReviewedAppointmentVersion).HasColumnType("binary(8)");
    }
}
public sealed class AttendanceFeedbackResponseConfiguration : IEntityTypeConfiguration<AttendanceFeedbackResponse>
{
    public void Configure(EntityTypeBuilder<AttendanceFeedbackResponse> b)
    {
        b.ToTable("AttendanceFeedbackResponses", t =>
        {
            t.HasTrigger("TR_AttendanceFeedbackResponses_Immutable"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_AttendanceFeedbackResponses_Facts", "[Revision]>0 AND [Outcome] IN (1,2,3) AND (([Revision]=1 AND [IsEdit]=0) OR ([Revision]>1 AND [IsEdit]=1))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<AttendanceFeedbackRequest>().WithMany().HasForeignKey(x => new { x.FeedbackRequestId, x.ActorUserId })
            .HasPrincipalKey(x => new { x.Id, x.PatientUserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FeedbackRequestId, x.SubmissionId }).IsUnique();
        b.HasIndex(x => new { x.FeedbackRequestId, x.Revision }).IsUnique();
        b.Property(x => x.PreviousVersion).HasColumnType("binary(8)").IsRequired();
    }
}
