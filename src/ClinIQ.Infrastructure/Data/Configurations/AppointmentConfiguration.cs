using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Patients;
using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.ToTable("Appointments", t =>
        {
            t.HasCheckConstraint("CK_Appointments_Selection", "[StartsAtUtc]<[EndsAtUtc] AND [StartsLocal]<[EndsLocal] AND [BookingClosesAtUtc]<=[EndsAtUtc] AND [Fee]>=0 AND [Currency]='EGP' AND [TimeZoneId]='Africa/Cairo' AND LEN([ContactName])>0 AND LEN([ContactPhone]) BETWEEN 7 AND 16");
            t.HasCheckConstraint("CK_Appointments_Status", "[Status] BETWEEN 1 AND 7 AND ([ReplacesAppointmentId] IS NULL OR [ReplacesAppointmentId]<>[Id])");
            t.HasCheckConstraint("CK_Appointments_Attendance", "([QueueNumber] IS NULL OR [QueueNumber]>0) AND (([ArrivedAtUtc] IS NULL AND [CheckInRecordedAtUtc] IS NULL) OR ([ArrivedAtUtc] IS NOT NULL AND [CheckInRecordedAtUtc] IS NOT NULL AND [QueueNumber] IS NOT NULL AND [ArrivedAtUtc]>=[StartsAtUtc] AND [ArrivedAtUtc]<=[CheckInRecordedAtUtc])) AND ([Status] NOT IN (2,3,7) OR [ArrivedAtUtc] IS NOT NULL) AND ([Status] NOT IN (1,5,6) OR [ArrivedAtUtc] IS NULL) AND (([Status]=3 AND [CompletedAtUtc] IS NOT NULL AND [CompletionRecordedAtUtc] IS NOT NULL AND [ArrivedAtUtc] IS NOT NULL AND [CompletedAtUtc]>=[ArrivedAtUtc] AND [CompletedAtUtc]<=[CompletionRecordedAtUtc]) OR ([Status]<>3 AND [CompletedAtUtc] IS NULL AND [CompletionRecordedAtUtc] IS NULL)) AND (([LateAuthorizedByUserId] IS NULL AND [LateAuthorizedAtUtc] IS NULL) OR ([LateAuthorizedByUserId] IS NOT NULL AND [LateAuthorizedAtUtc] IS NOT NULL AND [ArrivedAtUtc] IS NOT NULL AND [LateAuthorizedAtUtc]>=[ArrivedAtUtc])) AND ([ArrivedAtUtc] IS NULL OR [ArrivedAtUtc]<[EndsAtUtc] OR [LateAuthorizedByUserId] IS NOT NULL) AND ([LateAuthorizedByUserId] IS NULL OR [LateAuthorizedByUserId]=[DoctorUserId])");
            t.HasCheckConstraint("CK_Appointments_Cancellation", "([Status]=4 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [StatusBeforeCancellation] IS NOT NULL AND [StatusBeforeCancellation] IN (1,2) AND [CancellationReason] IS NOT NULL AND LEN([CancellationReason])>0 AND [CancellationCategory] IS NOT NULL) OR ([Status]<>4 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [StatusBeforeCancellation] IS NULL AND [CancellationReason] IS NULL AND [CancellationCategory] IS NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion();
        b.Ignore(x => x.ConsumesCapacity); b.Ignore(x => x.Selection); b.Ignore(x => x.Attendance);
        b.HasAlternateKey(x => new { x.Id, x.SessionId, x.DoctorProfileId, x.PatientId });
        b.HasAlternateKey(x => new { x.Id, x.PatientId }); b.HasAlternateKey(x => new { x.Id, x.PatientId, x.DoctorProfileId });
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => new { x.PatientId, x.PatientUserId }).HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BookingSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.LocationId, x.DoctorProfileId, x.DoctorUserId }).HasPrincipalKey(x => new { x.Id, x.LocationId, x.DoctorProfileId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => new { x.ReplacesAppointmentId, x.PatientId, x.DoctorProfileId }).HasPrincipalKey(x => new { x.Id, x.PatientId, x.DoctorProfileId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.LateAuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.SessionId }).IsUnique().HasFilter("[Status] IN (1,2,3,6,7)");
        b.HasIndex(x => new { x.SessionId, x.QueueNumber }).IsUnique().HasFilter("[QueueNumber] IS NOT NULL");
        b.HasIndex(x => x.ReplacesAppointmentId).IsUnique().HasFilter("[ReplacesAppointmentId] IS NOT NULL");
        b.HasIndex(x => new { x.PatientId, x.StartsAtUtc, x.Id }); b.HasIndex(x => new { x.SessionId, x.Status });
        b.Property(x => x.ContactName).HasMaxLength(200).IsRequired(); b.Property(x => x.ContactPhone).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(x => x.DoctorName).HasMaxLength(200).IsRequired(); b.Property(x => x.SpecialtyAr).HasMaxLength(100).IsRequired(); b.Property(x => x.SpecialtyEn).HasMaxLength(100).IsRequired();
        b.Property(x => x.PlaceName).HasMaxLength(200).IsRequired(); b.Property(x => x.Address).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsRequired(); b.Property(x => x.Fee).HasPrecision(18, 2);
        b.Property(x => x.TimeZoneId).HasMaxLength(50).IsUnicode(false).IsRequired(); b.Property(x => x.StartsLocal).HasColumnType("time(0)"); b.Property(x => x.EndsLocal).HasColumnType("time(0)");
        b.Property(x => x.CancellationReason).HasMaxLength(2000); b.Property(x => x.CancellationCategory).HasMaxLength(50).IsUnicode(false);
    }
}
public sealed class AppointmentOperationConfiguration : IEntityTypeConfiguration<AppointmentOperation>
{
    public void Configure(EntityTypeBuilder<AppointmentOperation> b)
    {
        b.ToTable("AppointmentOperations", t => t.HasCheckConstraint("CK_AppointmentOperations_Kind", "[Kind] IN (1,2,3) AND LEN([RequestHash])=64"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        b.HasIndex(x => new { x.PatientId, x.RequestId }).IsUnique();
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => new { x.ResultAppointmentId, x.PatientId }).HasPrincipalKey(x => new { x.Id, x.PatientId }).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class BookingNoticeConfiguration : IEntityTypeConfiguration<BookingNotice>
{
    public void Configure(EntityTypeBuilder<BookingNotice> b)
    {
        b.ToTable("BookingNotices", t => t.HasCheckConstraint("CK_BookingNotices_State", "[Kind] IN (1,2,3) AND [Attempts]>=0 AND [DeliveryStatus] IN ('Pending','Sending','DevelopmentFile','Unavailable')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AppointmentId, x.RecipientUserId, x.Kind }).IsUnique();
        b.HasIndex(x => new { x.DeliveryStatus, x.LastAttemptUtc }); b.Property(x => x.DeliveryStatus).HasMaxLength(30).IsUnicode(false).IsRequired();
    }
}
