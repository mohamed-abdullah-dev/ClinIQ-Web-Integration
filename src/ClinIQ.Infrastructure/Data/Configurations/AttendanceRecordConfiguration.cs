using ClinIQ.Domain.Appointments;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> b)
    {
        b.ToTable("AttendanceRecords", t =>
        {
            t.HasTrigger("TR_AttendanceRecords_Immutable"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_AttendanceRecords_Facts", "[Revision]>0 AND [Action] BETWEEN 1 AND 6 AND [PreviousStatus] BETWEEN 1 AND 7 AND [NewStatus] BETWEEN 1 AND 7 AND LEN([Reason])>0 AND ISJSON([BeforeFactsJson])=1 AND ISJSON([AfterFactsJson])=1");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.AppointmentId, x.Revision });
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => new { x.AppointmentId, x.SessionId, x.DoctorProfileId, x.PatientId }).HasPrincipalKey(x => new { x.Id, x.SessionId, x.DoctorProfileId, x.PatientId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Reason).HasMaxLength(2000).IsRequired(); b.Property(x => x.PreviousVersion).HasColumnType("binary(8)").IsRequired();
        b.Property(x => x.BeforeFactsJson).HasMaxLength(4000).IsRequired(); b.Property(x => x.AfterFactsJson).HasMaxLength(4000).IsRequired();
        b.HasIndex(x => new { x.AppointmentId, x.PreviousVersion }).IsUnique(); b.HasIndex(x => new { x.AppointmentId, x.RecordedAtUtc, x.Id });
        b.HasIndex(x => new { x.AppointmentId, x.Revision }).IsUnique();
    }
}
