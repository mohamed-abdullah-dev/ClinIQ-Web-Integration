using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace ClinIQ.Infrastructure.Data.Configurations;
public sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    public void Configure(EntityTypeBuilder<PushDevice> b)
    {
        b.ToTable("PushDevices", t => t.HasCheckConstraint("CK_PushDevices_Binding", "[Platform] IN (1,2) AND [BindingRevision]>0 AND LEN([InstallationSecretHash])=64 AND (([UserId] IS NULL AND [SessionId] IS NULL AND [TokenHash] IS NULL AND [ProtectedToken] IS NULL) OR ([UserId] IS NOT NULL AND [SessionId] IS NOT NULL AND [TokenHash] IS NOT NULL AND [ProtectedToken] IS NOT NULL))"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsRowVersion();
        b.HasOne<AuthSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.UserId }).HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.InstallationSecretHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength(); b.Property(x => x.ProtectedToken).HasMaxLength(8192);
        b.HasIndex(x => x.TokenHash).IsUnique().HasFilter("[TokenHash] IS NOT NULL"); b.HasIndex(x => x.UserId);
    }
}
public sealed class FeedbackNoticeConfiguration : IEntityTypeConfiguration<FeedbackNotice>
{
    public void Configure(EntityTypeBuilder<FeedbackNotice> b)
    {
        b.ToTable("FeedbackNotices", t => t.HasCheckConstraint("CK_FeedbackNotices_Kind", "[Kind] IN (1,2) AND [NextAttemptAtUtc]>=[DueAtUtc]"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<AttendanceFeedbackRequest>().WithMany().HasForeignKey(x => x.FeedbackRequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FeedbackRequestId, x.Kind }).IsUnique(); b.HasIndex(x => new { x.ExpandedAtUtc, x.CancelledAtUtc, x.NextAttemptAtUtc });
        b.Property(x => x.LastErrorCode).HasMaxLength(100).IsUnicode(false);
    }
}
public sealed class FeedbackDeliveryConfiguration : IEntityTypeConfiguration<FeedbackDelivery>
{
    public void Configure(EntityTypeBuilder<FeedbackDelivery> b)
    {
        b.ToTable("FeedbackDeliveries", t => t.HasCheckConstraint("CK_FeedbackDeliveries_State", "[State] BETWEEN 1 AND 6 AND [Attempts]>=0 AND (([DeviceId] IS NULL AND [SessionId] IS NULL AND [BindingRevision] IS NULL AND [TargetKey]='email') OR ([DeviceId] IS NOT NULL AND [SessionId] IS NOT NULL AND [BindingRevision]>0)) AND ([State]<>2 OR ([ClaimId] IS NOT NULL AND [LeaseUntilUtc] IS NOT NULL))"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<FeedbackNotice>().WithMany().HasForeignKey(x => x.NoticeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PushDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.TargetKey).HasMaxLength(32).IsUnicode(false); b.HasIndex(x => new { x.NoticeId, x.TargetKey }).IsUnique();
        b.HasIndex(x => new { x.State, x.NextAttemptAtUtc }); b.Property(x => x.LastErrorCode).HasMaxLength(100).IsUnicode(false); b.Property(x => x.ProviderMessageId).HasMaxLength(512);
    }
}
