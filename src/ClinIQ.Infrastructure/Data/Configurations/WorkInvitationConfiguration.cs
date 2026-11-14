using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Invitations;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class WorkInvitationConfiguration : IEntityTypeConfiguration<WorkInvitation>
{
    public void Configure(EntityTypeBuilder<WorkInvitation> b)
    {
        b.ToTable("WorkInvitations", t =>
        {
            t.HasCheckConstraint("CK_WorkInvitations_Scope", "([Kind]=1 AND [BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([Kind]=2 AND (([BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([BranchId] IS NULL AND [LocationId] IS NOT NULL)))");
            t.HasCheckConstraint("CK_WorkInvitations_Fee", "([Kind]=1 AND [ConsultationFee] IS NOT NULL AND [ConsultationFee]>=0 AND [Currency] IS NOT NULL AND [Currency]='EGP') OR ([Kind]=2 AND [ConsultationFee] IS NULL AND [Currency] IS NULL)");
            t.HasCheckConstraint("CK_WorkInvitations_Status", "([Status]=1 AND [ClosedAtUtc] IS NULL AND [ClosedByUserId] IS NULL AND [ClosureReason] IS NULL) OR ([Status] IN(2,3) AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL AND [ClosureReason] IS NULL) OR ([Status] IN(4,5,6) AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL AND [ClosureReason] IS NOT NULL AND LEN([ClosureReason])>0)");
            t.HasCheckConstraint("CK_WorkInvitations_Terms", "LEN([Terms])>0 AND [ExpiresAtUtc]>[CreatedAtUtc]");
            t.HasCheckConstraint("CK_WorkInvitations_Delivery", "[DeliveryStatus] IN('Pending','DevelopmentFile','Unavailable')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion();
        b.Property(x => x.Email).HasMaxLength(256).IsRequired(); b.Property(x => x.NormalizedEmail).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        b.Property(x => x.SecretHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired(); b.Property(x => x.Terms).HasMaxLength(2000).IsRequired();
        b.Property(x => x.ConsultationFee).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.ClosureReason).HasMaxLength(2000); b.Property(x => x.DeliveryStatus).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.IssuerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkInvitation>().WithMany().HasForeignKey(x => x.ReplacesInvitationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BranchId, x.Kind, x.NormalizedEmail }).IsUnique().HasFilter("[Status]=1 AND [BranchId] IS NOT NULL");
        b.HasIndex(x => new { x.LocationId, x.Kind, x.NormalizedEmail }).IsUnique().HasFilter("[Status]=1 AND [LocationId] IS NOT NULL");
        b.HasIndex(x => new { x.NormalizedEmail, x.CreatedAtUtc, x.Id });
    }
}
public sealed class ReceptionistAssignmentConfiguration : IEntityTypeConfiguration<ReceptionistAssignment>
{
    public void Configure(EntityTypeBuilder<ReceptionistAssignment> b)
    {
        b.ToTable("ReceptionistAssignments", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistAssignments_Scope", "([BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([BranchId] IS NULL AND [LocationId] IS NOT NULL)");
            t.HasCheckConstraint("CK_ReceptionistAssignments_Revocation", "([IsActive]=1 AND [RevokedByUserId] IS NULL AND [RevokedAtUtc] IS NULL AND [RevocationReason] IS NULL) OR ([IsActive]=0 AND [RevokedByUserId] IS NOT NULL AND [RevokedAtUtc] IS NOT NULL AND [RevocationReason] IS NOT NULL AND LEN([RevocationReason])>0)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion(); b.Property(x => x.RevocationReason).HasMaxLength(2000);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkInvitation>().WithMany().HasForeignKey(x => x.SourceInvitationId).OnDelete(DeleteBehavior.Restrict); b.HasIndex(x => x.SourceInvitationId).IsUnique();
        b.HasIndex(x => new { x.UserId, x.BranchId }).IsUnique().HasFilter("[IsActive]=1 AND [BranchId] IS NOT NULL");
        b.HasIndex(x => new { x.UserId, x.LocationId }).IsUnique().HasFilter("[IsActive]=1 AND [LocationId] IS NOT NULL");
    }
}
public sealed class PracticeFeeChangeConfiguration : IEntityTypeConfiguration<PracticeFeeChange>
{
    public void Configure(EntityTypeBuilder<PracticeFeeChange> b)
    {
        b.ToTable("PracticeFeeChanges", t =>
        {
            t.HasCheckConstraint("CK_PracticeFeeChanges_Price", "[PreviousFee]>=0 AND [NewFee]>=0 AND [PreviousFee]<>[NewFee] AND [Currency]='EGP'");
            t.HasCheckConstraint("CK_PracticeFeeChanges_Delivery", "[DeliveryStatus] IN('Pending','DevelopmentFile','Unavailable')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PreviousFee).HasPrecision(18, 2); b.Property(x => x.NewFee).HasPrecision(18, 2);
        b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired(); b.Property(x => x.DeliveryStatus).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DoctorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LocationId, x.CreatedAtUtc, x.Id });
    }
}
