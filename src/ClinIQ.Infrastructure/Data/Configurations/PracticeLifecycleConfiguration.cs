using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.PracticeLifecycle;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class OperationalScopeRecordConfiguration : IEntityTypeConfiguration<OperationalScopeRecord>
{
    public void Configure(EntityTypeBuilder<OperationalScopeRecord> b)
    {
        b.ToTable("OperationalScopeRecords", t =>
        {
            t.HasTrigger("TR_OperationalScopeRecords_Immutable"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_OperationalScopeRecords_Facts", "(([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)) AND LEN([Reason])>0 AND (([Action]='Begin' AND [BeforeState]=1 AND [AfterState]=2) OR ([Action]='Finalize' AND [BeforeState]=2 AND [AfterState]=3))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LocationId, x.Action }).IsUnique().HasFilter("[LocationId] IS NOT NULL");
        b.HasIndex(x => new { x.BranchId, x.Action }).IsUnique().HasFilter("[BranchId] IS NOT NULL");
        b.Property(x => x.Action).HasMaxLength(10).IsUnicode(false); b.Property(x => x.Reason).HasMaxLength(2000);
        b.Property(x => x.SourceVersion).HasColumnType("binary(8)");
    }
}
public sealed class OperationalEmergencyConfiguration : IEntityTypeConfiguration<OperationalEmergency>
{
    public void Configure(EntityTypeBuilder<OperationalEmergency> b)
    {
        b.ToTable("OperationalEmergencies", t =>
        {
            t.HasTrigger("TR_OperationalEmergencies_Protect"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_OperationalEmergencies_Facts", "(([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)) AND [Category] IN(1,2) AND LEN([Reason])>0 AND (([Status]=1 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedPreviewHash] IS NULL AND [ReviewedScopeVersion] IS NULL AND [CancelledCount] IS NULL) OR ([Status] IN(2,3) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[RequesterUserId] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason])>0 AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedPreviewHash] IS NOT NULL AND [ReviewedScopeVersion] IS NOT NULL AND [CancelledCount] IS NOT NULL AND [CancelledCount]>=0 AND ([Status]=2 OR [CancelledCount]=0)))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion();
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LocationId).IsUnique().HasFilter("[LocationId] IS NOT NULL AND [Status]=1");
        b.HasIndex(x => x.BranchId).IsUnique().HasFilter("[BranchId] IS NOT NULL AND [Status]=1");
        b.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
        b.Property(x => x.Reason).HasMaxLength(2000); b.Property(x => x.ReviewReason).HasMaxLength(2000);
        b.Property(x => x.ReviewedPreviewHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.Property(x => x.ReviewedScopeVersion).HasColumnType("binary(8)");
    }
}
public sealed class EmergencyCancellationRecordConfiguration : IEntityTypeConfiguration<EmergencyCancellationRecord>
{
    public void Configure(EntityTypeBuilder<EmergencyCancellationRecord> b)
    {
        b.ToTable("EmergencyCancellationRecords", t =>
        {
            t.HasTrigger("TR_EmergencyCancellationRecords_Immutable"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_EmergencyCancellationRecords_Json", "ISJSON([BeforeJson])=1 AND ISJSON([AfterJson])=1");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<OperationalEmergency>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AppointmentId).IsUnique();
        b.Property(x => x.SourceVersion).HasColumnType("binary(8)");
        b.Property(x => x.BeforeJson).HasMaxLength(32000); b.Property(x => x.AfterJson).HasMaxLength(32000);
    }
}
