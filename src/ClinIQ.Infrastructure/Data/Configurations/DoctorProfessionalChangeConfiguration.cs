using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class DoctorProfessionalChangeConfiguration : IEntityTypeConfiguration<DoctorProfessionalChange>
{
    public void Configure(EntityTypeBuilder<DoctorProfessionalChange> builder)
    {
        builder.ToTable("DoctorProfessionalChanges", table => table.HasCheckConstraint("CK_DoctorProfessionalChanges_Decision",
            "([Status] = 1 AND [ReviewerUserId] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewReason] IS NULL) OR ([Status] IN (2,3) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId] <> [UserId] AND [ReviewedAtUtc] IS NOT NULL AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000)"));
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProfessionalName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OriginalProfessionalName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OriginalProfileVersion).HasColumnType("binary(8)").IsRequired();
        builder.Property(x => x.ReviewReason).HasMaxLength(2000);
        builder.Property(x => x.Version).IsRowVersion();
        builder.HasIndex(x => x.DoctorProfileId).IsUnique().HasFilter("[Status] = 1");
        builder.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
        builder.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => new { x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Specialty>().WithMany().HasForeignKey(x => x.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Specialty>().WithMany().HasForeignKey(x => x.OriginalSpecialtyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
public sealed class ProfessionalChangeDocumentConfiguration : IEntityTypeConfiguration<ProfessionalChangeDocument>
{
    public void Configure(EntityTypeBuilder<ProfessionalChangeDocument> builder)
    {
        builder.ToTable("ProfessionalChangeDocuments"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.StorageKey).HasMaxLength(160).IsRequired();
        builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.MediaType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(x => new { x.RequestId, x.Kind }).IsUnique(); builder.HasIndex(x => x.StorageKey).IsUnique();
    }
}
public sealed class ProfessionalChangeEvidenceAccessConfiguration : IEntityTypeConfiguration<ProfessionalChangeEvidenceAccess>
{
    public void Configure(EntityTypeBuilder<ProfessionalChangeEvidenceAccess> builder)
    {
        builder.ToTable("ProfessionalChangeEvidenceAccesses"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne<ProfessionalChangeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ViewerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.DocumentId, x.GrantedAtUtc });
    }
}
