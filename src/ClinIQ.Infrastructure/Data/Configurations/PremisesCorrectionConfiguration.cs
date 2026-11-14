using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.PremisesCorrections;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class PremisesCorrectionConfiguration : IEntityTypeConfiguration<PremisesCorrection>
{
    public void Configure(EntityTypeBuilder<PremisesCorrection> b)
    {
        b.ToTable("PremisesCorrections", t =>
        {
            t.HasCheckConstraint("CK_PremisesCorrections_Scope", "([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)");
            t.HasCheckConstraint("CK_PremisesCorrections_Reasons", "LEN([RequestReason]) BETWEEN 1 AND 2000");
            t.HasCheckConstraint("CK_PremisesCorrections_Decision", "([Status]=1 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [SamePremisesVerified]=0 AND [LicenseVerified]=0 AND [AuthorityVerified]=0) OR ([Status] IN (2,3,4) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[ApplicantUserId] AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewReason] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>2 OR ([SamePremisesVerified]=1 AND [LicenseVerified]=1 AND [AuthorityVerified]=1)))");
            foreach (var prefix in new[] { "Original", "Proposed" })
            {
                t.HasCheckConstraint($"CK_PremisesCorrections_{prefix}_Coordinates", $"([{prefix}_Latitude] IS NULL AND [{prefix}_Longitude] IS NULL) OR ([{prefix}_Latitude] IS NOT NULL AND [{prefix}_Longitude] IS NOT NULL AND [{prefix}_Latitude] BETWEEN -90 AND 90 AND [{prefix}_Longitude] BETWEEN -180 AND 180)");
                t.HasCheckConstraint($"CK_PremisesCorrections_{prefix}_Details", $"LEN([{prefix}_Name])>0 AND LEN([{prefix}_Address])>0 AND (([BranchId] IS NULL AND [{prefix}_PublicPhone] IS NULL AND [{prefix}_LicensingAuthority] IS NULL AND [{prefix}_LicenseNumber] IS NULL) OR ([BranchId] IS NOT NULL AND [{prefix}_PublicPhone] IS NOT NULL AND LEN([{prefix}_PublicPhone])>0 AND [{prefix}_LicensingAuthority] IS NOT NULL AND LEN([{prefix}_LicensingAuthority])>0 AND [{prefix}_LicenseNumber] IS NOT NULL AND LEN([{prefix}_LicenseNumber])>0))");
            }
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsRowVersion(); b.Property(x => x.OriginalVersion).HasColumnType("binary(8)").IsRequired();
        b.Property(x => x.RequestReason).HasMaxLength(2000).IsRequired(); b.Property(x => x.ReviewReason).HasMaxLength(2000);
        b.OwnsOne(x => x.Original, Details); b.Navigation(x => x.Original).IsRequired();
        b.OwnsOne(x => x.Proposed, Details); b.Navigation(x => x.Proposed).IsRequired();
        b.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApplicantUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LocationId).IsUnique().HasFilter("[Status]=1 AND [LocationId] IS NOT NULL");
        b.HasIndex(x => x.BranchId).IsUnique().HasFilter("[Status]=1 AND [BranchId] IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id }); b.HasIndex(x => new { x.ApplicantUserId, x.CreatedAtUtc, x.Id });
        b.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.CorrectionId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
    private static void Details(OwnedNavigationBuilder<PremisesCorrection, PremisesDetails> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired(); b.Property(x => x.Address).HasMaxLength(500).IsRequired();
        b.Property(x => x.Latitude).HasPrecision(12, 9); b.Property(x => x.Longitude).HasPrecision(12, 9);
        b.Property(x => x.PublicPhone).HasMaxLength(16); b.Property(x => x.LicensingAuthority).HasMaxLength(100); b.Property(x => x.LicenseNumber).HasMaxLength(100);
    }
}
public sealed class PremisesCorrectionDocumentConfiguration : IEntityTypeConfiguration<PremisesCorrectionDocument>
{
    public void Configure(EntityTypeBuilder<PremisesCorrectionDocument> b)
    {
        b.ToTable("PremisesCorrectionDocuments"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.StorageKey).HasMaxLength(160).IsRequired(); b.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.MediaType).HasMaxLength(100).IsRequired(); b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.HasIndex(x => new { x.CorrectionId, x.Kind }).IsUnique(); b.HasIndex(x => x.StorageKey).IsUnique();
    }
}
public sealed class PremisesCorrectionAccessConfiguration : IEntityTypeConfiguration<PremisesCorrectionEvidenceAccess>
{
    public void Configure(EntityTypeBuilder<PremisesCorrectionEvidenceAccess> b)
    {
        b.ToTable("PremisesCorrectionEvidenceAccesses"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<PremisesCorrectionDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ViewerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DocumentId, x.GrantedAtUtc });
    }
}
