using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class DoctorProfileConfiguration : IEntityTypeConfiguration<DoctorProfile>
{
    public void Configure(EntityTypeBuilder<DoctorProfile> builder)
    {
        builder.ToTable("DoctorProfiles"); builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Version).IsRowVersion();
        builder.HasAlternateKey(row => new { row.Id, row.UserId });
        builder.Property(row => row.ProfessionalName).HasMaxLength(200).IsRequired();
        builder.HasIndex(row => row.UserId).IsUnique();
        builder.HasIndex(row => row.DoctorRegistrationId).IsUnique();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DoctorRegistration>().WithMany().HasForeignKey(row => row.DoctorRegistrationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Specialty>().WithMany().HasForeignKey(row => row.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DoctorRegistrationReviewConfiguration : IEntityTypeConfiguration<DoctorRegistrationReview>
{
    public void Configure(EntityTypeBuilder<DoctorRegistrationReview> builder)
    {
        builder.ToTable("DoctorRegistrationReviews", table => table.HasCheckConstraint("CK_DoctorRegistrationReviews_Outcome", "[Outcome] IN (2, 3)"));
        builder.HasKey(row => row.Id); builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Reason).HasMaxLength(2000).IsRequired();
        builder.HasIndex(row => row.DoctorRegistrationId).IsUnique();
        builder.HasOne<DoctorRegistration>().WithMany().HasForeignKey(row => row.DoctorRegistrationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(row => row.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DoctorEvidenceAccessConfiguration : IEntityTypeConfiguration<DoctorEvidenceAccess>
{
    public void Configure(EntityTypeBuilder<DoctorEvidenceAccess> builder)
    {
        builder.ToTable("DoctorEvidenceAccesses"); builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.HasIndex(row => new { row.DocumentId, row.GrantedAtUtc });
        builder.HasOne<DoctorEvidenceDocument>().WithMany().HasForeignKey(row => row.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(row => row.ViewerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
