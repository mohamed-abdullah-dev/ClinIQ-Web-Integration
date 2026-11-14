using ClinIQ.Domain.Clinics;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

internal static class ClinicDetailMapping
{
    public static void Apply(EntityTypeBuilder builder)
    {
        builder.Ignore(nameof(ClinicRegistrationRequest.Details));
        foreach (var field in new[] { "ClinicName", "BranchName", "LicensedOperator" }) builder.Property<string>(field).HasMaxLength(200).IsRequired();
        builder.Property<string>("Description").HasMaxLength(2000).IsRequired(); builder.Property<string>("Address").HasMaxLength(500).IsRequired();
        builder.Property<decimal?>("Latitude").HasPrecision(12, 9); builder.Property<decimal?>("Longitude").HasPrecision(12, 9);
        builder.Property<string>("PublicPhone").HasMaxLength(16).IsRequired();
        builder.Property<string>("LicensingAuthority").HasMaxLength(100).IsRequired(); builder.Property<string>("LicenseNumber").HasMaxLength(100).IsRequired();
    }
}
public sealed class ClinicRegistrationRequestConfiguration : IEntityTypeConfiguration<ClinicRegistrationRequest>
{
    public void Configure(EntityTypeBuilder<ClinicRegistrationRequest> builder)
    {
        builder.ToTable("ClinicRegistrationRequests", t =>
        {
            t.HasCheckConstraint("CK_ClinicRegistrationRequests_State", "([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL)");
            t.HasCheckConstraint("CK_ClinicRegistrationRequests_Approval", "([Status]=5 AND [ApprovedClinicId] IS NOT NULL AND [ApprovedBranchId] IS NOT NULL AND ([TargetClinicId] IS NULL OR [TargetClinicId]=[ApprovedClinicId])) OR ([Status]<>5 AND [ApprovedClinicId] IS NULL AND [ApprovedBranchId] IS NULL)");
        });
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever(); builder.HasAlternateKey(x => new { x.Id, x.ApplicantUserId });
        ClinicDetailMapping.Apply(builder); builder.Property(x => x.Version).IsRowVersion();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApplicantUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(x => x.TargetClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ApprovedClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinicBranch>().WithMany().HasForeignKey(x => new { x.ApprovedBranchId, x.ApprovedClinicId })
            .HasPrincipalKey(x => new { x.Id, x.ClinicId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinicRegistrationSubmission>().WithMany().HasForeignKey(x => new { x.CurrentSubmissionId, x.Id })
            .HasPrincipalKey(x => new { x.Id, x.RequestId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ApplicantUserId, x.CreatedAtUtc, x.Id }); builder.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
    }
}
public sealed class ClinicRegistrationSubmissionConfiguration : IEntityTypeConfiguration<ClinicRegistrationSubmission>
{
    public void Configure(EntityTypeBuilder<ClinicRegistrationSubmission> builder)
    {
        builder.ToTable("ClinicRegistrationSubmissions", t => t.HasCheckConstraint("CK_ClinicRegistrationSubmissions_Decision",
            "([Status]=2 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [LicenseDetailsVerified]=0 AND [IdentityVerified]=0 AND [AuthorityVerified]=0 AND [IndependentlyCorroborated]=0) OR ([Status] IN (3,4,5) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[ApplicantUserId] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>5 OR ([LicenseDetailsVerified]=1 AND [IdentityVerified]=1 AND [AuthorityVerified]=1 AND [IndependentlyCorroborated]=1)))"));
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasAlternateKey(x => new { x.Id, x.RequestId }); builder.HasAlternateKey(x => new { x.Id, x.ApplicantUserId });
        ClinicDetailMapping.Apply(builder); builder.Property(x => x.ReviewReason).HasMaxLength(2000);
        builder.HasOne<ClinicRegistrationRequest>().WithMany().HasForeignKey(x => new { x.RequestId, x.ApplicantUserId })
            .HasPrincipalKey(x => new { x.Id, x.ApplicantUserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(x => x.TargetClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.RequestId, x.AttemptNumber }).IsUnique(); builder.HasIndex(x => x.RequestId).IsUnique().HasFilter("[Status] = 2");
        builder.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
public sealed class ClinicRegistrationDocumentConfiguration : IEntityTypeConfiguration<ClinicRegistrationDocument>
{
    public void Configure(EntityTypeBuilder<ClinicRegistrationDocument> builder)
    {
        builder.ToTable("ClinicRegistrationDocuments"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.StorageKey).HasMaxLength(160).IsRequired(); builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.MediaType).HasMaxLength(100).IsRequired(); builder.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(x => new { x.SubmissionId, x.Kind }).IsUnique(); builder.HasIndex(x => x.StorageKey).IsUnique();
    }
}
public sealed class ClinicEvidenceAccessConfiguration : IEntityTypeConfiguration<ClinicEvidenceAccess>
{
    public void Configure(EntityTypeBuilder<ClinicEvidenceAccess> builder)
    {
        builder.ToTable("ClinicEvidenceAccesses"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne<ClinicRegistrationDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ViewerUserId).OnDelete(DeleteBehavior.Restrict); builder.HasIndex(x => new { x.DocumentId, x.GrantedAtUtc });
    }
}
public sealed class ClinicConfiguration : IEntityTypeConfiguration<Clinic>
{
    public void Configure(EntityTypeBuilder<Clinic> builder)
    {
        builder.ToTable("Clinics"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired(); builder.Property(x => x.Description).HasMaxLength(2000).IsRequired(); builder.Property(x => x.Version).IsRowVersion();
        builder.HasAlternateKey(x => new { x.Id, x.SourceSubmissionId }); builder.HasIndex(x => x.SourceSubmissionId).IsUnique();
        builder.HasOne<ClinicRegistrationSubmission>().WithMany().HasForeignKey(x => x.SourceSubmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ClinicBranchConfiguration : IEntityTypeConfiguration<ClinicBranch>
{
    public void Configure(EntityTypeBuilder<ClinicBranch> builder)
    {
        builder.ToTable("ClinicBranches", t => t.HasCheckConstraint("CK_ClinicBranches_Lifecycle", "[LifecycleState] IN (1,2,3) AND ([LifecycleState]<>3 OR [IsActive]=0)"));
        builder.Property(x => x.LifecycleState).HasDefaultValue(BranchLifecycleState.Open).HasSentinel(BranchLifecycleState.Open);
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever(); builder.HasAlternateKey(x => new { x.Id, x.ClinicId });
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired(); builder.Property(x => x.Address).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Latitude).HasPrecision(12, 9); builder.Property(x => x.Longitude).HasPrecision(12, 9);
        builder.Property(x => x.PublicPhone).HasMaxLength(16).IsRequired(); builder.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired(); builder.Property(x => x.Version).IsRowVersion();
        foreach (var field in new[] { "LicensingAuthority", "LicenseNumber", "NormalizedLicensingAuthority", "NormalizedLicenseNumber" }) builder.Property<string>(field).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NormalizedLicensingAuthority).UseCollation("Latin1_General_100_BIN2"); builder.Property(x => x.NormalizedLicenseNumber).UseCollation("Latin1_General_100_BIN2");
        builder.HasIndex(x => new { x.NormalizedLicensingAuthority, x.NormalizedLicenseNumber }).IsUnique(); builder.HasIndex(x => x.SourceSubmissionId).IsUnique();
        builder.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinicRegistrationSubmission>().WithMany().HasForeignKey(x => x.SourceSubmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ClinicManagementMembershipConfiguration : IEntityTypeConfiguration<ClinicManagementMembership>
{
    public void Configure(EntityTypeBuilder<ClinicManagementMembership> builder)
    {
        builder.ToTable("ClinicManagementMemberships"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => x.ClinicId).IsUnique().HasFilter("[IsActive] = 1"); builder.HasIndex(x => new { x.ClinicId, x.UserId }).IsUnique();
        builder.HasOne<Clinic>().WithMany().HasForeignKey(x => new { x.ClinicId, x.SourceSubmissionId })
            .HasPrincipalKey(x => new { x.Id, x.SourceSubmissionId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ClinicRegistrationSubmission>().WithMany().HasForeignKey(x => new { x.SourceSubmissionId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.ApplicantUserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
