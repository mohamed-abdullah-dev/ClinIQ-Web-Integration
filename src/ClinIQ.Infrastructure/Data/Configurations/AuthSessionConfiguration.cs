using ClinIQ.Domain.Accounts;
using ClinIQ.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.ToTable("AuthSessions", t => { t.HasTrigger("TR_AuthSessions_DetachPush"); t.UseSqlOutputClause(false); });
        builder.HasAlternateKey(x => new { x.Id, x.UserId });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(256);
        builder.Property(x => x.AccessHash).IsRequired().HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
    }
}
public sealed class SessionRefreshTokenConfiguration : IEntityTypeConfiguration<SessionRefreshToken>
{
    public void Configure(EntityTypeBuilder<SessionRefreshToken> builder)
    {
        builder.ToTable("SessionRefreshTokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SecretHash).IsRequired().HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        builder.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}
