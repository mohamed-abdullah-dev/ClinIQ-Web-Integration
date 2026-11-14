using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class PracticeLocationConfiguration : IEntityTypeConfiguration<DoctorPracticeLocation>
{
    public void Configure(EntityTypeBuilder<DoctorPracticeLocation> builder)
    {
        builder.ToTable("DoctorPracticeLocations", table =>
        {
            table.HasCheckConstraint("CK_DoctorPracticeLocations_Lifecycle", "[LifecycleState] IN (1,2,3) AND ([LifecycleState]<>3 OR [IsActive]=0)");
            table.HasCheckConstraint("CK_DoctorPracticeLocations_State", "([ClinicBranchId] IS NULL AND [SourceInvitationId] IS NULL AND [SourceLinkId] IS NULL AND (([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL))) OR ([ClinicBranchId] IS NOT NULL AND [Status]=5 AND (([SourceInvitationId] IS NOT NULL AND [SourceLinkId] IS NULL AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([SourceInvitationId] IS NULL AND [SourceLinkId] IS NOT NULL AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL)))");
            table.HasCheckConstraint("CK_DoctorPracticeLocations_Price", "[ConsultationFee]>=0 AND [Currency]='EGP'");
            table.HasCheckConstraint("CK_DoctorPracticeLocations_Coordinates", "([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL AND [Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)");
        });
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired(); builder.Property(x => x.Address).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Latitude).HasPrecision(12, 9); builder.Property(x => x.Longitude).HasPrecision(12, 9);
        builder.Property(x => x.ConsultationFee).HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength().UseCollation("Latin1_General_100_BIN2").IsRequired();
        builder.Property(x => x.Version).IsRowVersion();
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.LifecycleState).HasDefaultValue(PracticeLifecycleState.Active).HasSentinel(PracticeLifecycleState.Active);
        builder.HasOne<ClinIQ.Domain.Clinics.ClinicBranch>().WithMany().HasForeignKey(x => x.ClinicBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinIQ.Domain.Invitations.WorkInvitation>().WithMany().HasForeignKey(x => x.SourceInvitationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.SourceInvitationId).IsUnique().HasFilter("[SourceInvitationId] IS NOT NULL");
        builder.HasOne<ClinIQ.Domain.PracticeLinks.PracticeLinkRequest>().WithMany().HasForeignKey(x => new { x.SourceLinkId, x.Id, x.ClinicBranchId })
            .HasPrincipalKey(x => new { x.Id, x.LocationId, x.BranchId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.DoctorProfileId, x.ClinicBranchId }).IsUnique().HasFilter("[ClinicBranchId] IS NOT NULL");
        builder.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => new { x.DoctorProfileId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PracticeLocationSubmission>().WithMany().HasForeignKey(x => new { x.CurrentSubmissionId, x.Id })
            .HasPrincipalKey(x => new { x.Id, x.LocationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.CreatedAtUtc, x.Id }); builder.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
    }
}

public sealed class PracticeLocationSubmissionConfiguration : IEntityTypeConfiguration<PracticeLocationSubmission>
{
    public void Configure(EntityTypeBuilder<PracticeLocationSubmission> builder)
    {
        builder.ToTable("PracticeLocationSubmissions", table =>
        {
            table.HasCheckConstraint("CK_PracticeLocationSubmissions_Decision", "([Status]=2 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [LicenseMatchesAddress]=0 AND [DoctorWorkLinkVerified]=0) OR ([Status] IN (3,4,5) AND [ReviewerUserId] IS NOT NULL AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>5 OR ([LicenseMatchesAddress]=1 AND [DoctorWorkLinkVerified]=1)))");
            table.HasCheckConstraint("CK_PracticeLocationSubmissions_Price", "[ConsultationFee]>=0 AND [Currency]='EGP'");
            table.HasCheckConstraint("CK_PracticeLocationSubmissions_Attempt", "[AttemptNumber]>0");
        });
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever(); builder.HasAlternateKey(x => new { x.Id, x.LocationId });
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired(); builder.Property(x => x.Address).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Latitude).HasPrecision(12, 9); builder.Property(x => x.Longitude).HasPrecision(12, 9);
        builder.Property(x => x.ConsultationFee).HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength().UseCollation("Latin1_General_100_BIN2").IsRequired();
        builder.Property(x => x.ReviewReason).HasMaxLength(2000);
        builder.HasOne<DoctorPracticeLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.LocationId, x.AttemptNumber }).IsUnique();
        builder.HasIndex(x => x.LocationId).IsUnique().HasFilter("[Status] = 2");
        builder.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
public sealed class PracticeLocationDocumentConfiguration : IEntityTypeConfiguration<PracticeLocationDocument>
{
    public void Configure(EntityTypeBuilder<PracticeLocationDocument> builder)
    {
        builder.ToTable("PracticeLocationDocuments"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.StorageKey).HasMaxLength(160).IsRequired(); builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.MediaType).HasMaxLength(100).IsRequired(); builder.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(x => new { x.SubmissionId, x.Kind }).IsUnique(); builder.HasIndex(x => x.StorageKey).IsUnique();
    }
}
public sealed class PracticeLocationEvidenceAccessConfiguration : IEntityTypeConfiguration<PracticeLocationEvidenceAccess>
{
    public void Configure(EntityTypeBuilder<PracticeLocationEvidenceAccess> builder)
    {
        builder.ToTable("PracticeLocationEvidenceAccesses"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne<PracticeLocationDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ViewerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.DocumentId, x.GrantedAtUtc });
    }
}
