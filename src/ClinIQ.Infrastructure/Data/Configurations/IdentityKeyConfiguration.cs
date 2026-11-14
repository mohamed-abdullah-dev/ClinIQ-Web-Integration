using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinIQ.Infrastructure.Data.Configurations;

public sealed class IdentityKeyConfiguration :
    IEntityTypeConfiguration<IdentityUserLogin<Guid>>,
    IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    private const int KeyPartMaxLength = 128;

    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.Property(login => login.LoginProvider).HasMaxLength(KeyPartMaxLength);
        builder.Property(login => login.ProviderKey).HasMaxLength(KeyPartMaxLength);
    }

    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.Property(token => token.LoginProvider).HasMaxLength(KeyPartMaxLength);
        builder.Property(token => token.Name).HasMaxLength(KeyPartMaxLength);
    }
}
