using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class DoctorProTrialConfiguration : IEntityTypeConfiguration<DoctorProTrial>
{
    public void Configure(EntityTypeBuilder<DoctorProTrial> builder)
    {
        builder.ToTable("DoctorProTrials", table =>
        {
            table.HasTrigger("TR_DoctorProTrials_Immutable");
            table.HasCheckConstraint("CK_DoctorProTrials_UtcDuration",
                "DATEPART(TZOFFSET, [ActivatedAtUtc]) = 0 AND DATEPART(TZOFFSET, [ExpiresAtUtc]) = 0 AND [ExpiresAtUtc] = DATEADD(day, 30, [ActivatedAtUtc])");
        });
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("DoctorProfileId").ValueGeneratedNever();
        builder.Property(t => t.TermsVersion).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.HasOne<DoctorProfile>().WithOne().HasForeignKey<DoctorProTrial>(t => t.Id).OnDelete(DeleteBehavior.Restrict);
    }
}
