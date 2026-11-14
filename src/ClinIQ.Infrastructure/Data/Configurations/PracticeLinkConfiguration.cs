using ClinIQ.Domain.PracticeLinks;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Clinics;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class PracticeLinkConfiguration : IEntityTypeConfiguration<PracticeLinkRequest>
{
    public void Configure(EntityTypeBuilder<PracticeLinkRequest> b)
    {
        b.ToTable("PracticeLinkRequests", t =>
        {
            t.HasTrigger("TR_PracticeLinkRequests_Protect"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_PracticeLinkRequests_Decision", "([Status]=1 AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [DecisionReason] IS NULL) OR ([Status] IN (2,3,4) AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc]>=[RequestedAtUtc] AND [DecidedAtUtc] IS NOT NULL AND [DecisionReason] IS NOT NULL AND LEN([DecisionReason]) BETWEEN 1 AND 2000 AND ([Status]<>4 OR [DecidedByUserId]=[DoctorUserId]))");
            t.HasCheckConstraint("CK_PracticeLinkRequests_Terms", "[Terms]='SamePremises-ManagedFee-NewSessionsOnly-v1' AND [ConsultationFee]>=0 AND [Currency]='EGP'");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.LocationId, x.BranchId });
        b.Property(x => x.LocationVersion).HasColumnType("binary(8)").IsRequired(); b.Property(x => x.BranchVersion).HasColumnType("binary(8)").IsRequired();
        b.Property(x => x.Version).IsRowVersion(); b.Property(x => x.Terms).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.LocationName).HasMaxLength(200); b.Property(x => x.BranchName).HasMaxLength(200);
        b.Property(x => x.LocationAddress).HasMaxLength(500); b.Property(x => x.BranchAddress).HasMaxLength(500);
        b.Property(x => x.ConsultationFee).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        b.Property(x => x.DecisionReason).HasMaxLength(2000);
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DoctorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LocationId).IsUnique().HasFilter("[Status]=1");
        b.HasIndex(x => new { x.BranchId, x.Status, x.RequestedAtUtc, x.Id });
        b.HasIndex(x => new { x.DoctorUserId, x.RequestedAtUtc, x.Id });
    }
}
