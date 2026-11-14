using ClinIQ.Domain.Specialties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class SpecialtyConfiguration : IEntityTypeConfiguration<Specialty>
{
    private const string NormalizedNameCollation = "Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<Specialty> builder)
    {
        builder.ToTable("Specialties");

        builder.HasKey(specialty => specialty.Id);
        builder.Property(specialty => specialty.Id).ValueGeneratedNever();
        builder.Property(specialty => specialty.Version).IsRowVersion();

        builder.Property(specialty => specialty.NameAr)
            .IsRequired()
            .HasMaxLength(Specialty.NameMaxLength)
            .IsUnicode();

        builder.Property(specialty => specialty.NameEn)
            .IsRequired()
            .HasMaxLength(Specialty.NameMaxLength)
            .IsUnicode();

        builder.Property(specialty => specialty.NormalizedNameAr)
            .IsRequired()
            .HasMaxLength(Specialty.NameMaxLength)
            .IsUnicode()
            .UseCollation(NormalizedNameCollation);

        builder.Property(specialty => specialty.NormalizedNameEn)
            .IsRequired()
            .HasMaxLength(Specialty.NameMaxLength)
            .IsUnicode()
            .UseCollation(NormalizedNameCollation);

        builder.HasIndex(specialty => specialty.NormalizedNameAr).IsUnique();
        builder.HasIndex(specialty => specialty.NormalizedNameEn).IsUnique();
    }
}
