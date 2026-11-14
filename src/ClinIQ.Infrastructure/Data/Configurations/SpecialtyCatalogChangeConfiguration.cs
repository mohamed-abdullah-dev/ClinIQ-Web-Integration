using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class SpecialtyCatalogChangeConfiguration : IEntityTypeConfiguration<SpecialtyCatalogChange>
{
    public void Configure(EntityTypeBuilder<SpecialtyCatalogChange> builder)
    {
        builder.ToTable("SpecialtyCatalogChanges", table => table.HasCheckConstraint("CK_SpecialtyCatalogChanges_Operation", "[Operation] IN (1,2,3)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PreviousNameAr).HasMaxLength(Specialty.NameMaxLength);
        builder.Property(x => x.PreviousNameEn).HasMaxLength(Specialty.NameMaxLength);
        builder.Property(x => x.NameAr).HasMaxLength(Specialty.NameMaxLength);
        builder.Property(x => x.NameEn).HasMaxLength(Specialty.NameMaxLength);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.SpecialtyId, x.OccurredAtUtc });
    }
}
