using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterDoctor;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class DoctorRegistrationService(AppDbContext context, UserManager<AppUser> users,
    RegistrationStorage storage, AccountEmailConfirmationService email, ILogger<DoctorRegistrationService> logger)
    : IDoctorRegistrationService
{
    public async Task<Result<DoctorRegistrationReceipt>> RegisterAsync(RegisterDoctorCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Registration requires a context without pending changes.");
        if (!await context.Specialties.AnyAsync(specialty => specialty.Id == command.SpecialtyId, cancellationToken))
            return AccountErrors.UnknownSpecialty;
        if (await users.FindByEmailAsync(command.Email.Trim()) is not null) return AccountErrors.EmailTaken;
        var userId = Guid.NewGuid();
        var registrationId = Guid.NewGuid();
        var prepared = new List<PreparedEvidence>();
        foreach (var upload in command.Documents)
        {
            var document = await storage.ReadAsync(registrationId, upload, cancellationToken);
            if (document.IsError) return Result<DoctorRegistrationReceipt>.Failure(document.Errors);
            prepared.Add(document.Value);
        }
        var registration = DoctorRegistration.Create(registrationId, userId, command.FullName, command.PhoneNumber,
            command.RegistrationNumber, command.SpecialtyId, command.ProfessionalGrade,
            prepared.Select(document => document.Metadata).ToArray());
        if (registration.IsError) return Result<DoctorRegistrationReceipt>.Failure(registration.Errors);

        var user = new AppUser
        {
            Id = userId, FullName = command.FullName.Trim(), UserName = command.Email.Trim(),
            Email = command.Email.Trim(), PhoneNumber = command.PhoneNumber.Trim()
        };
        var written = new List<string>();
        var committed = false;
        var commitAttempted = false;
        try
        {
            // Validate the password before writing files or opening a database transaction.
            var passwordErrors = new List<IdentityError>();
            foreach (var validator in users.PasswordValidators)
            {
                var result = await validator.ValidateAsync(users, user, command.Password);
                if (!result.Succeeded) passwordErrors.AddRange(result.Errors);
            }
            if (passwordErrors.Count > 0) return IdentityFailure(passwordErrors);

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var created = await users.CreateAsync(user, command.Password);
            if (!created.Succeeded) return IdentityFailure(created.Errors);
            foreach (var document in prepared)
            {
                await storage.WriteAsync(document, cancellationToken);
                written.Add(document.Metadata.StorageKey);
            }
            context.DoctorRegistrations.Add(registration.Value);
            await context.SaveChangesAsync(cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql &&
            sql.Number is 2601 or 2627 &&
            (sql.Message.Contains("'EmailIndex'", StringComparison.Ordinal) ||
             sql.Message.Contains("'UserNameIndex'", StringComparison.Ordinal)))
        {
            // SQL is the final guard for concurrent normalized-email registration.
            return AccountErrors.EmailTaken;
        }
        finally
        {
            if (!committed && !commitAttempted)
            {
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError("Uncommitted evidence cleanup failed with {ExceptionType}; private-storage reconciliation is required.",
                            error.GetType().Name);
                        throw;
                    }
                }
            }
            else if (!committed)
            {
                // A lost commit acknowledgement cannot safely be treated as a rollback.
                logger.LogError("Registration commit outcome is uncertain; private evidence is retained for reconciliation.");
            }
        }

        var delivery = await email.SendAsync(user, cancellationToken);
        return new DoctorRegistrationReceipt(user.Id, registrationId, registration.Value.Status.ToString(), true, delivery);
    }

    private static Result<DoctorRegistrationReceipt> IdentityFailure(IEnumerable<IdentityError> errors) =>
        Result<DoctorRegistrationReceipt>.Failure(errors.Select(error =>
            error.Code is "DuplicateEmail" or "DuplicateUserName"
                ? AccountErrors.EmailTaken
                : Error.Validation($"Account.{error.Code}", error.Description,
                    error.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email")));
}
