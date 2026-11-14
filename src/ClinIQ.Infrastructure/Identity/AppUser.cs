using Microsoft.AspNetCore.Identity;

namespace ClinIQ.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
}
