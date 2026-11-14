using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class DoctorRegistrationConfiguration : IEntityTypeConfiguration<DoctorRegistration>
{
    public void Configure(EntityTypeBuilder<DoctorRegistration> builder)
    {
        builder.ToTable("DoctorRegistrations");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.ProfessionalName).HasMaxLength(200).IsRequired();
        builder.Property(row => row.PhoneNumber).HasMaxLength(32).IsRequired();
        builder.Property(row => row.RegistrationNumber).HasMaxLength(100).IsRequired();
        builder.Property(row => row.Version).IsRowVersion();
        builder.HasIndex(row => new { row.Status, row.CreatedAtUtc, row.Id });
        builder.Property(row => row.IsCurrent).HasDefaultValue(true);
        builder.Property(row => row.AttemptNumber).HasDefaultValue(1);
        builder.HasAlternateKey(row => new { row.Id, row.UserId });
        builder.HasIndex(row => row.UserId).IsUnique().HasFilter("[IsCurrent] = 1");
        builder.HasIndex(row => new { row.UserId, row.AttemptNumber }).IsUnique();
        builder.HasIndex(row => row.PreviousRegistrationId).IsUnique().HasFilter("[PreviousRegistrationId] IS NOT NULL");
        builder.HasOne<DoctorRegistration>().WithMany().HasForeignKey(row => new { row.PreviousRegistrationId, row.UserId })
            .HasPrincipalKey(row => new { row.Id, row.UserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(row => row.ResubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(table => table.HasCheckConstraint("CK_DoctorRegistrations_Attempt",
            "([AttemptNumber] = 1 AND [PreviousRegistrationId] IS NULL AND [ResubmittedByUserId] IS NULL) OR " +
            "([AttemptNumber] > 1 AND [PreviousRegistrationId] IS NOT NULL AND [ResubmittedByUserId] IS NOT NULL AND [ResubmittedByUserId] = [UserId])"));
        builder.HasOne<AppUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinIQ.Domain.Specialties.Specialty>().WithMany()
            .HasForeignKey(row => row.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(row => row.Documents).WithOne().HasForeignKey(row => row.DoctorRegistrationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(row => row.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class DoctorEvidenceDocumentConfiguration : IEntityTypeConfiguration<DoctorEvidenceDocument>
{
    public void Configure(EntityTypeBuilder<DoctorEvidenceDocument> builder)
    {
        builder.ToTable("DoctorEvidenceDocuments");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.StorageKey).HasMaxLength(160).IsRequired();
        builder.Property(row => row.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(row => row.MediaType).HasMaxLength(100).IsRequired();
        builder.Property(row => row.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(row => new { row.DoctorRegistrationId, row.Kind }).IsUnique();
        builder.HasIndex(row => row.StorageKey).IsUnique();
    }
}

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(user => user.FullName).HasMaxLength(200).IsRequired();
        builder.HasIndex(user => user.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");
    }
}
