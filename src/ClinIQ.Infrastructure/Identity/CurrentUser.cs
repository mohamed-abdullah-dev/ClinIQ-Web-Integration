using System.Security.Claims;
using ClinIQ.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace ClinIQ.Infrastructure.Identity;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true) return null;
            var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
                throw new InvalidOperationException("Authenticated identity has no valid user ID.");
            return userId;
        }
    }
}
