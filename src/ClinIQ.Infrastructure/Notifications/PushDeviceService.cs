using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Notifications;
using ClinIQ.Application.Features.Notifications.Commands;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Invitations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Notifications;

internal sealed class PushDeviceService(AppDbContext db, ICurrentUser current, IHttpContextAccessor http,
    IDataProtectionProvider protection, WorkforceAccess locks, TimeProvider clock) : IPushDeviceService
{
    private async Task<AuthSession?> ActorAsync(CancellationToken ct)
    {
        if (current.UserId is not Guid user || http.HttpContext?.User.FindFirst("client")?.Value != "Mobile" ||
            !Guid.TryParse(http.HttpContext.User.FindFirst("session_id")?.Value, out var id)) return null;
        var now = clock.GetUtcNow();
        return await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.UserId == user && s.Client == SessionClient.Mobile &&
            s.RevokedAtUtc == null && s.ExpiresAtUtc > now && s.AccessExpiresAtUtc > now && db.Users.Any(u => u.Id == user && u.EmailConfirmed && u.SecurityStamp == s.SecurityStamp) &&
            db.UserRoles.Any(ur => ur.UserId == user && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == "PATIENT")), ct);
    }
    public async Task<Result<PushDeviceDto>> RegisterAsync(RegisterPushDeviceInput input, CancellationToken ct)
    {
        locks.Fresh();
        if (input is null || !(await new PushDeviceInputValidator().ValidateAsync(input, ct)).IsValid) return NotificationErrors.Invalid;
        var actor = await ActorAsync(ct); if (actor is null) return NotificationErrors.Unauthorized;
        var tokenHash = SessionSecrets.Hash(input.Token);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await locks.LockAsync($"ClinIQ.AccountCredentials.{actor.UserId:N}", ct) || !await locks.LockAsync($"ClinIQ.Session.{actor.Id:N}", ct) ||
            !await locks.LockAsync($"ClinIQ.PushToken.{tokenHash}", ct) || !await locks.LockAsync($"ClinIQ.PushInstallation.{input.InstallationId:N}", ct)) return NotificationErrors.Busy;
        actor = await ActorAsync(ct); if (actor is null) return NotificationErrors.Unauthorized;
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Id == input.InstallationId, ct);
        if (device is not null && !SessionSecrets.Matches(input.InstallationSecret, device.InstallationSecretHash)) return NotificationErrors.Proof;
        if (await db.PushDevices.AnyAsync(d => d.Id != input.InstallationId && d.TokenHash == tokenHash, ct)) return NotificationErrors.TokenInUse;
        if (device is null) { device = PushDevice.Create(input.InstallationId, SessionSecrets.Hash(input.InstallationSecret)); db.PushDevices.Add(device); }
        device.Bind(actor.UserId, actor.Id, tokenHash, protection.CreateProtector("ClinIQ.PushToken.v1").Protect(input.Token), input.Platform, clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(device); }
        catch (DbUpdateConcurrencyException) { return NotificationErrors.Busy; }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 }) { return NotificationErrors.TokenInUse; }
    }
    public async Task<Result<Success>> DetachAsync(Guid id, CancellationToken ct)
    {
        locks.Fresh(); var actor = await ActorAsync(ct); if (actor is null) return NotificationErrors.Unauthorized;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await locks.LockAsync($"ClinIQ.Session.{actor.Id:N}", ct) || !await locks.LockAsync($"ClinIQ.PushInstallation.{id:N}", ct)) return NotificationErrors.Busy;
        if (await ActorAsync(ct) is null) return NotificationErrors.Unauthorized;
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Id == id && d.UserId == actor.UserId && d.SessionId == actor.Id, ct);
        if (device is null) return NotificationErrors.NotFound;
        device.Detach(clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Result.Success; }
        catch (DbUpdateConcurrencyException) { return NotificationErrors.Busy; }
    }
    public async Task<Result<IReadOnlyList<PushDeviceDto>>> ListAsync(CancellationToken ct)
    {
        var actor = await ActorAsync(ct); if (actor is null) return NotificationErrors.Unauthorized;
        var now = clock.GetUtcNow();
        var devices = await db.PushDevices.AsNoTracking().Where(d => d.UserId == actor.UserId && db.AuthSessions.Any(s => s.Id == d.SessionId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now))
            .OrderBy(d => d.Id).Take(100).ToArrayAsync(ct);
        return Result<IReadOnlyList<PushDeviceDto>>.Success(devices.Select(Dto).ToArray());
    }
    private static PushDeviceDto Dto(PushDevice device) => new(device.Id, device.SessionId!.Value, device.Platform, device.BindingRevision);
}
