using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class DoctorResubmissionGrantConfiguration : IEntityTypeConfiguration<DoctorResubmissionGrant>
{
    public void Configure(EntityTypeBuilder<DoctorResubmissionGrant> builder)
    {
        builder.ToTable("DoctorResubmissionGrants");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.SecretHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(row => row.SecurityStamp).HasMaxLength(256).IsRequired();
        builder.Property(row => row.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(row => row.RegistrationVersion).HasMaxLength(8).IsFixedLength().IsRequired();
        builder.HasOne<DoctorRegistration>().WithMany().HasForeignKey(row => new { row.RegistrationId, row.UserId })
            .HasPrincipalKey(row => new { row.Id, row.UserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(row => row.RegistrationId).IsUnique().HasFilter("[ConsumedAtUtc] IS NULL");
    }
}
