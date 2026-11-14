using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Invitations;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Clinics;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Invitations;

internal sealed class ReceptionistRegistrationService(AppDbContext db, WorkforceAccess access, UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles, AccountEmailConfirmationService email, TimeProvider clock) : IReceptionistRegistrationService
{
    public async Task<Result<ReceptionistRegistrationReceipt>> RegisterAsync(Guid invitationId, string secret, string fullName, string address, string password, CancellationToken ct)
    {
        access.Fresh(); var normalized = users.NormalizeEmail(address.Trim());
        var invitation = await db.WorkInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitationId && x.Kind == WorkInvitationKind.Receptionist && x.NormalizedEmail == normalized, ct);
        if (invitation is null || !InvitationValidation.Secret(secret) || !SessionSecrets.Matches(secret, invitation.SecretHash)) return InvitationErrors.NotFound;
        var existing = await users.FindByEmailAsync(address.Trim());
        var user = existing ?? new AppUser { Id = Guid.NewGuid(), FullName = fullName.Trim(), Email = address.Trim(), UserName = address.Trim(), EmailConfirmed = false };
        if (invitation.TargetUserId.HasValue && invitation.TargetUserId != user.Id) return InvitationErrors.NotFound;
        if (existing is null)
        {
            var errors = new List<IdentityError>();
            foreach (var validator in users.PasswordValidators) { var result = await validator.ValidateAsync(users, user, password); if (!result.Succeeded) errors.AddRange(result.Errors); }
            if (errors.Count > 0) return Failure(errors);
        }
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await access.LockAsync($"ClinIQ.AccountCredentials.{user.Id:N}", ct)) return InvitationErrors.Busy;
            if (!await access.LockAsync(WorkforceAccess.ScopeLock(invitation.BranchId, invitation.LocationId), ct)) return InvitationErrors.Busy;
            invitation = await db.WorkInvitations.AsNoTracking().SingleAsync(x => x.Id == invitationId, ct);
            if (invitation.Status != WorkInvitationStatus.Pending) return InvitationErrors.Closed;
            if (clock.GetUtcNow() >= invitation.ExpiresAtUtc) return InvitationErrors.Expired;
            if (!await access.CanIssueAsync(invitation.IssuerUserId, invitation.Kind, invitation.BranchId, invitation.LocationId, ct)) return InvitationErrors.Forbidden;
            if (existing is not null)
            {
                await db.Entry(user).ReloadAsync(ct);
                if (db.Entry(user).State == EntityState.Detached || user.NormalizedEmail != normalized || await users.IsLockedOutAsync(user)) return SessionErrors.InvalidCredentials;
                if (!await users.CheckPasswordAsync(user, password))
                {
                    if (user.LockoutEnabled && !(await users.AccessFailedAsync(user)).Succeeded) return SessionErrors.InvalidCredentials;
                    await tx.CommitAsync(ct); return SessionErrors.InvalidCredentials;
                }
                if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded) return SessionErrors.InvalidCredentials;
            }
            if (!await new ClinicRoleProvisioner(db, roles).EnsureAsync("Receptionist", ct)) return InvitationErrors.Busy;
            if (existing is null) { var created = await users.CreateAsync(user, password); if (!created.Succeeded) return Failure(created.Errors); }
            if (!await users.IsInRoleAsync(user, "Receptionist") && !(await users.AddToRoleAsync(user, "Receptionist")).Succeeded) throw new InvalidOperationException("Receptionist signup role assignment failed.");
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 } sql && (sql.Message.Contains("'EmailIndex'", StringComparison.Ordinal) || sql.Message.Contains("'UserNameIndex'", StringComparison.Ordinal)))
        { return AccountErrors.EmailTaken; }
        return new ReceptionistRegistrationReceipt(user.Id, !user.EmailConfirmed, user.EmailConfirmed ? "NotRequired" : await email.SendAsync(user, ct));
    }
    private static Result<ReceptionistRegistrationReceipt> Failure(IEnumerable<IdentityError> errors) => Result<ReceptionistRegistrationReceipt>.Failure(errors.Select(e =>
        e.Code is "DuplicateEmail" or "DuplicateUserName" ? AccountErrors.EmailTaken : Error.Validation($"Account.{e.Code}", e.Description, e.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email")));
}
