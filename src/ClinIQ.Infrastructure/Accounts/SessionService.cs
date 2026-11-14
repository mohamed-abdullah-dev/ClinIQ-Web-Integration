using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class SessionService(AppDbContext db, UserManager<AppUser> users, TimeProvider clock) : ISessionService
{
    // An unknown email still performs a password hash verification. Never log credentials or token values.
    private static readonly AppUser DummyUser = new();
    private static readonly PasswordHasher<AppUser> DummyHasher = new();
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, SessionSecrets.Create());

    public async Task<Result<SessionGrant>> LoginAsync(string email, string password, bool rememberMe, SessionClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            _ = DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return SessionErrors.InvalidCredentials;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Share the account lock with password recovery, across API processes. The first read may be stale.
        if (!await LockResourceAsync($"ClinIQ.AccountCredentials.{user.Id:N}", ct)) return SessionErrors.InvalidCredentials;
        await db.Entry(user).ReloadAsync(ct);
        if (db.Entry(user).State == EntityState.Detached) return SessionErrors.InvalidCredentials;
        if (await users.IsLockedOutAsync(user)) return SessionErrors.InvalidCredentials;
        if (!await users.CheckPasswordAsync(user, password))
        {
            if (user.LockoutEnabled)
            {
                var failed = await users.AccessFailedAsync(user);
                if (!failed.Succeeded) return SessionErrors.InvalidCredentials;
            }
            await transaction.CommitAsync(ct);
            return SessionErrors.InvalidCredentials;
        }
        var reset = await users.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded) return SessionErrors.InvalidCredentials;

        var roleResult = await EligibleUserAsync(user, client, ct);
        if (roleResult.IsError)
        {
            await transaction.CommitAsync(ct);
            return Result<SessionGrant>.Failure(roleResult.Errors);
        }
        var now = clock.GetUtcNow();
        var access = SessionSecrets.Create();
        var refresh = SessionSecrets.Create();
        var session = AuthSession.Create(user.Id, client, user.SecurityStamp!, SessionSecrets.Hash(access), now, rememberMe);
        var refreshRow = SessionRefreshToken.Create(session.Id, SessionSecrets.Hash(refresh));
        db.AuthSessions.Add(session);
        db.SessionRefreshTokens.Add(refreshRow);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Grant(session, roleResult.Value, access, refreshRow.Id, refresh);
    }

    public async Task<AuthenticatedSession?> AuthenticateAsync(string token, SessionClient client, CancellationToken ct)
    {
        if (!SessionSecrets.TryParse(token, out var id, out var secret)) return null;
        var session = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var now = clock.GetUtcNow();
        if (session is null || session.Client != client || !session.IsActive(now) || now >= session.AccessExpiresAtUtc ||
            !SessionSecrets.Matches(secret, session.AccessHash)) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.UserId, ct);
        if (user is null || user.SecurityStamp != session.SecurityStamp) return null;
        var eligible = await EligibleUserAsync(user, client, ct);
        return eligible.IsSuccess ? new AuthenticatedSession(session.Id, client, eligible.Value) : null;
    }

    public async Task<Result<SessionGrant>> RefreshAsync(string token, SessionClient client, CancellationToken ct)
    {
        if (!SessionSecrets.TryParse(token, out var id, out var secret)) return SessionErrors.InvalidSession;
        var proof = await db.SessionRefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (proof is null || !SessionSecrets.Matches(secret, proof.SecretHash)) return SessionErrors.InvalidSession;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(proof.SessionId, ct)) return SessionErrors.Busy;
        var session = await db.AuthSessions.SingleAsync(x => x.Id == proof.SessionId, ct);
        var current = await db.SessionRefreshTokens.SingleAsync(x => x.Id == id, ct);
        var now = clock.GetUtcNow();
        if (session.Client != client || !session.IsActive(now)) return SessionErrors.InvalidSession;
        if (current.ConsumedAtUtc is not null)
        {
            session.Revoke(now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return SessionErrors.InvalidSession;
        }
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.UserId, ct);
        if (user is null || user.SecurityStamp != session.SecurityStamp)
        {
            session.Revoke(now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return SessionErrors.InvalidSession;
        }
        var eligible = await EligibleUserAsync(user, client, ct);
        if (eligible.IsError)
        {
            session.Revoke(now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return SessionErrors.InvalidSession;
        }
        var access = SessionSecrets.Create();
        var refresh = SessionSecrets.Create();
        current.Consume(now);
        session.RotateAccess(SessionSecrets.Hash(access), now);
        var next = SessionRefreshToken.Create(session.Id, SessionSecrets.Hash(refresh));
        db.SessionRefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Grant(session, eligible.Value, access, next.Id, refresh);
    }

    // A refresh-token proof permits logout even after the short access credential expires.
    public async Task<Result<Success>> LogoutAsync(string token, SessionClient client, CancellationToken ct)
    {
        if (!SessionSecrets.TryParse(token, out var id, out var secret)) return Result.Success;
        var proof = await db.SessionRefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (proof is null || !SessionSecrets.Matches(secret, proof.SecretHash)) return Result.Success;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(proof.SessionId, ct)) return SessionErrors.Busy;
        var session = await db.AuthSessions.SingleAsync(x => x.Id == proof.SessionId, ct);
        if (session.Client != client) return Result.Success;
        session.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }

    private async Task<Result<SessionUser>> EligibleUserAsync(AppUser user, SessionClient client, CancellationToken ct)
    {
        var roles = await users.GetRolesAsync(user);
        var status = await db.DoctorRegistrations.AsNoTracking().Where(x => x.UserId == user.Id && x.IsCurrent)
            .Select(x => (DoctorRegistrationStatus?)x.Status).SingleOrDefaultAsync(ct);
        var managesClinic = roles.Contains("ClinicManager", StringComparer.Ordinal) &&
            await db.ClinicManagementMemberships.AsNoTracking().AnyAsync(m => m.UserId == user.Id && m.IsActive &&
                db.Clinics.Any(c => c.Id == m.ClinicId && c.IsActive), ct);
        var receptionist = roles.Contains("Receptionist", StringComparer.Ordinal) &&
            (await db.ReceptionistAssignments.AnyAsync(a => a.UserId == user.Id && a.IsActive, ct) ||
             await db.WorkInvitations.AnyAsync(i => i.Kind == ClinIQ.Domain.Invitations.WorkInvitationKind.Receptionist &&
                i.Status == ClinIQ.Domain.Invitations.WorkInvitationStatus.Pending && i.ExpiresAtUtc > clock.GetUtcNow() &&
                i.NormalizedEmail == user.NormalizedEmail && (i.TargetUserId == null || i.TargetUserId == user.Id), ct));
        var eligible = roles.Where(role => (client == SessionClient.Web && role == "Admin") ||
            (client == SessionClient.Mobile && role == "Patient") ||
            (client == SessionClient.Web && role == "Doctor" && status == DoctorRegistrationStatus.Approved) ||
            (client == SessionClient.Web && role == "ClinicManager" && managesClinic) ||
            (client == SessionClient.Web && role == "Receptionist" && receptionist))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // A later doctor application must not disable an independently approved Admin/Patient role.
        if (eligible.Length == 0 && status == DoctorRegistrationStatus.PendingReview) return SessionErrors.PendingReview;
        if (eligible.Length == 0 && status == DoctorRegistrationStatus.Rejected) return SessionErrors.DoctorRejected;
        if (!user.EmailConfirmed) return SessionErrors.EmailUnconfirmed;
        if (eligible.Length == 0 && client == SessionClient.Web && roles.Contains("ClinicApplicant", StringComparer.Ordinal))
        {
            var clinicStatus = await db.ClinicRegistrationRequests.AsNoTracking().Where(x => x.ApplicantUserId == user.Id && x.TargetClinicId == null)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Select(x => (ClinIQ.Domain.Clinics.ClinicApplicationStatus?)x.Status).FirstOrDefaultAsync(ct);
            return clinicStatus switch
            {
                ClinIQ.Domain.Clinics.ClinicApplicationStatus.Rejected => SessionErrors.ClinicRejected,
                ClinIQ.Domain.Clinics.ClinicApplicationStatus.NeedsInformation => SessionErrors.ClinicNeedsInformation,
                ClinIQ.Domain.Clinics.ClinicApplicationStatus.PendingReview => SessionErrors.ClinicPendingReview,
                _ => SessionErrors.ClientNotAllowed
            };
        }
        if (eligible.Length == 0) return SessionErrors.ClientNotAllowed;
        return new SessionUser(user.Id, user.FullName, user.Email!, eligible);
    }

    private Task<bool> LockAsync(Guid sessionId, CancellationToken ct) =>
        LockResourceAsync($"ClinIQ.Session.{sessionId:N}", ct);

    private async Task<bool> LockResourceAsync(string resource, CancellationToken ct)
    {
        // Transaction-owned SQL Server locks coordinate writers in separate API processes.
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        if (codes.Single() < -3) throw new InvalidOperationException("Unexpected session lock failure.");
        ct.ThrowIfCancellationRequested();
        return codes.Single() >= 0;
    }

    private static SessionGrant Grant(AuthSession session, SessionUser user, string access, Guid refreshId, string refresh) =>
        new(session.Id, session.Client, user, SessionSecrets.Format(session.Id, access), SessionSecrets.Format(refreshId, refresh),
            session.AccessExpiresAtUtc, session.ExpiresAtUtc, session.RememberMe);
}
