using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class FirstAdminBootstrapper(AppDbContext context, UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles, AccountEmailConfirmationService email,
    IHostEnvironment environment, ILogger<FirstAdminBootstrapper> logger)
{
    private const string AdminRole = "Admin";

    public async Task<Result<AdminBootstrapReceipt>> BootstrapAsync(AdminBootstrapInput input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!environment.IsDevelopment()) return AdminBootstrapErrors.DevelopmentOnly;
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Admin bootstrap requires a context without pending changes.");
        var validation = await new AdminBootstrapInputValidator().ValidateAsync(input, cancellationToken);
        if (!validation.IsValid)
            return Result<AdminBootstrapReceipt>.Failure(validation.Errors.Select(failure =>
                Error.Validation("AdminBootstrap.InvalidInput", failure.ErrorMessage, failure.PropertyName)));
        if ((await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            return AdminBootstrapErrors.MigrationsPending;

        var user = new AppUser
        {
            Id = Guid.NewGuid(), FullName = input.FullName.Trim(),
            UserName = input.Email.Trim(), Email = input.Email.Trim(), EmailConfirmed = false
        };
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var codes = await context.Database.SqlQueryRaw<int>("""
                DECLARE @code int;
                EXEC @code = sys.sp_getapplock @Resource=N'ClinIQ.AdminBootstrap',
                    @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
                SELECT @code AS [Value];
                """).ToArrayAsync(cancellationToken);
            var code = codes.Single();
            if (code < -3) throw new InvalidOperationException("Unexpected admin-bootstrap lock failure.");
            cancellationToken.ThrowIfCancellationRequested();
            if (code < 0) return AdminBootstrapErrors.Busy;

            // Role existence is the durable latch, even if its last member was removed.
            // Existing accounts are never promoted, repaired, or given a new password here.
            if (await roles.RoleExistsAsync(AdminRole)) return AdminBootstrapErrors.AlreadyInitialized;
            if (await users.FindByEmailAsync(user.Email) is not null ||
                await users.FindByNameAsync(user.UserName) is not null)
                return AccountErrors.EmailTaken;

            var created = await users.CreateAsync(user, input.Password);
            if (!created.Succeeded)
                return Result<AdminBootstrapReceipt>.Failure(created.Errors.Select(error =>
                    error.Code is "DuplicateEmail" or "DuplicateUserName" ? AccountErrors.EmailTaken :
                        Error.Validation($"Account.{error.Code}", error.Description,
                            error.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email")));
            var role = await roles.CreateAsync(new IdentityRole<Guid>(AdminRole) { Id = Guid.NewGuid() });
            if (!role.Succeeded) throw new InvalidOperationException("Admin role provisioning failed.");
            var assigned = await users.AddToRoleAsync(user, AdminRole);
            if (!assigned.Succeeded) throw new InvalidOperationException("Admin role assignment failed.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql &&
            sql.Number is 2601 or 2627 &&
            (sql.Message.Contains("'EmailIndex'", StringComparison.Ordinal) ||
             sql.Message.Contains("'UserNameIndex'", StringComparison.Ordinal)))
        {
            return AccountErrors.EmailTaken;
        }

        logger.LogInformation("First-admin bootstrap committed for user {UserId}.", user.Id);
        string delivery;
        try { delivery = await email.SendAsync(user, cancellationToken); }
        catch (Exception error)
        {
            // Commit is known to have succeeded: a mail/configuration failure cannot undo it.
            // Report the account as created, with delivery unavailable; public resend can retry.
            logger.LogWarning("Admin confirmation delivery failed with {ExceptionType}.", error.GetType().Name);
            delivery = "Unavailable";
        }
        return new AdminBootstrapReceipt(user.Id, delivery);
    }
}
