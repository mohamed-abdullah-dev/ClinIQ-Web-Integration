using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterPatient;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class PatientRegistrationService(AppDbContext context, UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles, AccountEmailConfirmationService email) : IPatientRegistrationService
{
    private const string PatientRole = "Patient";

    public async Task<Result<PatientRegistrationReceipt>> RegisterAsync(RegisterPatientCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Registration requires a context without pending changes.");
        if (await users.FindByEmailAsync(command.Email.Trim()) is not null) return AccountErrors.EmailTaken;

        var user = new AppUser
        {
            Id = Guid.NewGuid(), FullName = command.FullName.Trim(),
            UserName = command.Email.Trim(), Email = command.Email.Trim(), EmailConfirmed = false
        };
        var passwordErrors = new List<IdentityError>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, command.Password);
            if (!result.Succeeded) passwordErrors.AddRange(result.Errors);
        }
        if (passwordErrors.Count > 0) return IdentityFailure(passwordErrors);

        // Only first-time role provisioning is serialized. Once Patient exists, registrations can run concurrently.
        var roleExists = await roles.RoleExistsAsync(PatientRole);
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!roleExists)
            {
                var lockCodes = await context.Database.SqlQueryRaw<int>("""
                    DECLARE @code int;
                    EXEC @code = sys.sp_getapplock @Resource=N'ClinIQ.Identity.Role.Patient',
                        @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
                    SELECT @code AS [Value];
                    """).ToArrayAsync(cancellationToken);
                var code = lockCodes.Single();
                if (code < -3) throw new InvalidOperationException("Unexpected patient-role lock failure.");
                cancellationToken.ThrowIfCancellationRequested();
                if (code < 0) return AccountErrors.RegistrationBusy;
                if (!await roles.RoleExistsAsync(PatientRole))
                {
                    var createdRole = await roles.CreateAsync(new IdentityRole<Guid>(PatientRole) { Id = Guid.NewGuid() });
                    if (!createdRole.Succeeded) throw new InvalidOperationException("Patient role provisioning failed.");
                }
            }
            var created = await users.CreateAsync(user, command.Password);
            if (!created.Succeeded) return IdentityFailure(created.Errors);
            var assigned = await users.AddToRoleAsync(user, PatientRole);
            if (!assigned.Succeeded) throw new InvalidOperationException("Patient role assignment failed.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql &&
            sql.Number is 2601 or 2627 &&
            (sql.Message.Contains("'EmailIndex'", StringComparison.Ordinal) ||
             sql.Message.Contains("'UserNameIndex'", StringComparison.Ordinal)))
        {
            return AccountErrors.EmailTaken;
        }

        // The account and its fixed role are committed before attempting email delivery.
        // Public registration never issues a session or accepts a role from the caller.
        var delivery = await email.SendAsync(user, cancellationToken);
        return new PatientRegistrationReceipt(user.Id, true, delivery);
    }

    private static Result<PatientRegistrationReceipt> IdentityFailure(IEnumerable<IdentityError> errors) =>
        Result<PatientRegistrationReceipt>.Failure(errors.Select(error =>
            error.Code is "DuplicateEmail" or "DuplicateUserName"
                ? AccountErrors.EmailTaken
                : Error.Validation($"Account.{error.Code}", error.Description,
                    error.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email")));
}
