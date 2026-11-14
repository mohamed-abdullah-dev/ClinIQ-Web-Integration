using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinIQ.Infrastructure.Identity;

public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = PasswordResetTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromHours(1);
    }
}

// Use Identity's token format/validation, with separate lifetime and protection purpose from email confirmation.
public sealed class PasswordResetTokenProvider(IDataProtectionProvider protection,
    IOptions<PasswordResetTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<AppUser>> logger)
    : DataProtectorTokenProvider<AppUser>(protection, Microsoft.Extensions.Options.Options.Create<DataProtectionTokenProviderOptions>(options.Value), logger)
{
    public const string ProviderName = "ClinIQ.PasswordReset";
}
