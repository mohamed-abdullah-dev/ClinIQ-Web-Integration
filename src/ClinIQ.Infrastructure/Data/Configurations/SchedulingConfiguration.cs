using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class ScheduleRuleConfiguration : IEntityTypeConfiguration<ScheduleRule>
{
    public void Configure(EntityTypeBuilder<ScheduleRule> b)
    {
        b.ToTable("ScheduleRules", t =>
        {
            t.HasCheckConstraint("CK_ScheduleRules_Settings", "[Day] BETWEEN 0 AND 6 AND [StartsLocal]<[EndsLocal] AND DATEPART(second,[StartsLocal])=0 AND DATEPART(second,[EndsLocal])=0 AND [MaxCapacity]>0 AND [BookingCutoffMinutes]>=0 AND ([EffectiveUntil] IS NULL OR [EffectiveUntil]>=[EffectiveFrom])");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion(); b.Ignore(x => x.Data);
        b.Property(x => x.StartsLocal).HasColumnType("time(0)"); b.Property(x => x.EndsLocal).HasColumnType("time(0)");
        b.HasAlternateKey(x => new { x.Id, x.LocationId, x.DoctorProfileId, x.UserId });
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => new { x.LocationId, x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.DoctorProfileId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DoctorProfileId, x.IsActive, x.Day }); b.HasIndex(x => x.LocationId);
    }
}
public sealed class BookingSessionConfiguration : IEntityTypeConfiguration<BookingSession>
{
    public void Configure(EntityTypeBuilder<BookingSession> b)
    {
        b.ToTable("BookingSessions", t =>
        {
            t.HasCheckConstraint("CK_BookingSessions_Window", "[StartsAtUtc]<[EndsAtUtc] AND [StartsLocal]<[EndsLocal] AND DATEPART(second,[StartsLocal])=0 AND DATEPART(second,[EndsLocal])=0 AND [TimeZoneId]='Africa/Cairo'");
            t.HasCheckConstraint("CK_BookingSessions_Capacity", "[MaxCapacity]>0 AND [AllocatedCount]>=0 AND [AllocatedCount]<=[MaxCapacity]");
            t.HasCheckConstraint("CK_BookingSessions_Queue", "[LastQueueNumber]>=0");
            t.HasCheckConstraint("CK_BookingSessions_Cutoff", "[BookingCutoffMinutes]>=0 AND DATEDIFF_BIG(second,[BookingClosesAtUtc],[EndsAtUtc])=CONVERT(bigint,[BookingCutoffMinutes])*60");
            t.HasCheckConstraint("CK_BookingSessions_Source", "([SourceRuleId] IS NULL AND [SourceRuleVersion] IS NULL) OR ([SourceRuleId] IS NOT NULL AND [SourceRuleVersion] IS NOT NULL)");
            t.HasCheckConstraint("CK_BookingSessions_State", "([Status]=1 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancellationReason] IS NULL) OR ([Status]=2 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancellationReason] IS NOT NULL AND LEN([CancellationReason])>0)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion();
        b.Property(x => x.SourceRuleVersion).HasColumnType("binary(8)"); b.Property(x => x.StartsLocal).HasColumnType("time(0)"); b.Property(x => x.EndsLocal).HasColumnType("time(0)");
        b.HasAlternateKey(x => new { x.Id, x.LocationId, x.DoctorProfileId, x.UserId });
        b.Property(x => x.TimeZoneId).HasMaxLength(50).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired(); b.Property(x => x.CancellationReason).HasMaxLength(2000);
        b.HasOne<ClinIQ.Domain.Clinics.ClinicBranch>().WithMany().HasForeignKey(x => x.OperationalBranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => new { x.LocationId, x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.DoctorProfileId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ScheduleRule>().WithMany().HasForeignKey(x => new { x.SourceRuleId, x.LocationId, x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.LocationId, x.DoctorProfileId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SourceRuleId, x.LocalDate }).IsUnique().HasFilter("[SourceRuleId] IS NOT NULL");
        b.HasIndex(x => new { x.DoctorProfileId, x.Status, x.StartsAtUtc, x.EndsAtUtc }); b.HasIndex(x => new { x.LocationId, x.LocalDate, x.Id });
    }
}
public sealed class ScheduleExceptionConfiguration : IEntityTypeConfiguration<ScheduleException>
{
    public void Configure(EntityTypeBuilder<ScheduleException> b)
    {
        b.ToTable("ScheduleExceptions", t => t.HasCheckConstraint("CK_ScheduleExceptions_Removal", "LEN([Reason])>0 AND (([IsActive]=1 AND [RemovedByUserId] IS NULL AND [RemovedAtUtc] IS NULL AND [RemovalReason] IS NULL) OR ([IsActive]=0 AND [RemovedByUserId] IS NOT NULL AND [RemovedAtUtc] IS NOT NULL AND [RemovalReason] IS NOT NULL AND LEN([RemovalReason])>0))"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion(); b.Property(x => x.Reason).HasMaxLength(2000).IsRequired(); b.Property(x => x.RemovalReason).HasMaxLength(2000);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => new { x.DoctorProfileId, x.UserId }).HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => new { x.LocationId, x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.DoctorProfileId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RemovedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DoctorProfileId, x.LocalDate }).IsUnique().HasFilter("[LocationId] IS NULL AND [IsActive]=1");
        b.HasIndex(x => new { x.LocationId, x.LocalDate }).IsUnique().HasFilter("[LocationId] IS NOT NULL AND [IsActive]=1");
    }
}
