using ClinIQ.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Clinics;

internal sealed class ClinicRoleProvisioner(AppDbContext db, RoleManager<IdentityRole<Guid>> roles)
{
    public async Task<bool> EnsureAsync(string role, CancellationToken ct)
    {
        if (role is not ("ClinicApplicant" or "ClinicManager" or "Receptionist")) throw new ArgumentException("Unsupported clinic/workforce role.", nameof(role));
        if (await roles.RoleExistsAsync(role)) return true;
        var resource = $"ClinIQ.Identity.Role.{role}";
        var result = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (result.Single() < -3) throw new InvalidOperationException("Unexpected clinic-role lock failure.");
        if (result.Single() < 0) return false;
        if (!await roles.RoleExistsAsync(role) && !(await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded)
            throw new InvalidOperationException("Clinic role provisioning failed.");
        return true;
    }
}
