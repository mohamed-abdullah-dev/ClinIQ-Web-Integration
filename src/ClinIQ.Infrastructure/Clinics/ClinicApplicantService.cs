using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Clinics;
using ClinIQ.Application.Features.Clinics.Commands;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Clinics;

// Public intake verifies existing credentials without creating a login session.
public sealed class ClinicApplicantService(AppDbContext db, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles,
    AccountEmailConfirmationService email, RegistrationStorage storage, ILogger<ClinicApplicantService> logger) : IClinicApplicantService
{
    public async Task<Result<ClinicApplicantReceipt>> RegisterAsync(string fullName, string address, string password,
        ClinicApplicationData input, IReadOnlyList<ClinicEvidenceUpload> documents, CancellationToken ct)
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Clinic intake requires a context without pending writes.");
        ct.ThrowIfCancellationRequested();
        var details = input.Normalize(); if (details.IsError) return Result<ClinicApplicantReceipt>.Failure(details.Errors);
        if (documents.Count is < 2 or > 3) return ClinicErrors.RequiredEvidence;
        var existing = await users.FindByEmailAsync(address.Trim());
        var user = existing ?? new AppUser { Id = Guid.NewGuid(), FullName = fullName.Trim(), Email = address.Trim(), UserName = address.Trim(), EmailConfirmed = false };
        if (existing is null)
        {
            var errors = new List<IdentityError>();
            foreach (var validator in users.PasswordValidators) { var valid = await validator.ValidateAsync(users, user, password); if (!valid.Succeeded) errors.AddRange(valid.Errors); }
            if (errors.Count > 0) return Failure(errors);
        }
        var written = new List<string>(); var committed = false; var commitAttempted = false;
        ClinicRegistrationRequest request; ClinicRegistrationSubmission submission;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await LockAsync($"ClinIQ.AccountCredentials.{user.Id:N}", ct)) return ClinicErrors.Busy;
            if (existing is not null)
            {
                await db.Entry(user).ReloadAsync(ct);
                if (db.Entry(user).State == EntityState.Detached || await users.IsLockedOutAsync(user)) return SessionErrors.InvalidCredentials;
                if (!await users.CheckPasswordAsync(user, password))
                {
                    if (user.LockoutEnabled && !(await users.AccessFailedAsync(user)).Succeeded) return SessionErrors.InvalidCredentials;
                    await tx.CommitAsync(ct); return SessionErrors.InvalidCredentials;
                }
                if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded) return SessionErrors.InvalidCredentials;
            }
            // Reuse the same application after rejection/information requests; never edit a pending/approved snapshot.
            var previous = await db.ClinicRegistrationRequests.Where(x => x.ApplicantUserId == user.Id && x.TargetClinicId == null)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            if (previous is not null && previous.Status is not (ClinicApplicationStatus.Rejected or ClinicApplicationStatus.NeedsInformation)) return ClinicErrors.DraftLocked;
            // Complete Identity saves before mutating a tracked application's CurrentSubmissionId.
            // The new submission must be tracked before any save can persist that FK.
            if (!await new ClinicRoleProvisioner(db, roles).EnsureAsync("ClinicApplicant", ct)) return ClinicErrors.Busy;
            if (existing is null)
            {
                var createdUser = await users.CreateAsync(user, password); if (!createdUser.Succeeded) return Failure(createdUser.Errors);
            }
            if (!await users.IsInRoleAsync(user, "ClinicApplicant") && !(await users.AddToRoleAsync(user, "ClinicApplicant")).Succeeded) throw new InvalidOperationException("Clinic-applicant role assignment failed.");
            if (previous is null)
            {
                var created = ClinicRegistrationRequest.Create(Guid.NewGuid(), user.Id, null, details.Value);
                if (created.IsError) return Result<ClinicApplicantReceipt>.Failure(created.Errors);
                request = created.Value;
            }
            else
            {
                if (!await LockAsync($"ClinIQ.ClinicApplication.{previous.Id:N}", ct)) return ClinicErrors.Busy;
                await db.Entry(previous).ReloadAsync(ct);
                var changed = previous.UpdateDraft(user.Id, details.Value); if (changed.IsError) return Result<ClinicApplicantReceipt>.Failure(changed.Errors);
                request = previous;
            }
            var submissionId = Guid.NewGuid(); var prepared = new List<PreparedClinicEvidence>();
            foreach (var document in documents)
            {
                var read = await storage.ReadAsync(submissionId, document, ct); if (read.IsError) return Result<ClinicApplicantReceipt>.Failure(read.Errors);
                prepared.Add(read.Value);
            }
            // Persist the initial draft inside this transaction before creating its submission.
            // The root points to its current submission and the submission points back to the root.
            if (previous is null)
            {
                db.ClinicRegistrationRequests.Add(request); await db.SaveChangesAsync(ct);
            }
            var submitted = request.Submit(submissionId, user.Id, prepared.Select(x => x.Metadata).ToArray());
            if (submitted.IsError) return Result<ClinicApplicantReceipt>.Failure(submitted.Errors);
            submission = submitted.Value;
            foreach (var document in prepared) { await storage.WriteAsync(document, ct); written.Add(document.Metadata.StorageKey); }
            db.ClinicRegistrationSubmissions.Add(submission); await db.SaveChangesAsync(ct);
            commitAttempted = true; await tx.CommitAsync(ct); committed = true;
        }
        catch (DbUpdateConcurrencyException) { return ClinicErrors.Stale; }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 } sql &&
            (sql.Message.Contains("'EmailIndex'", StringComparison.Ordinal) || sql.Message.Contains("'UserNameIndex'", StringComparison.Ordinal)))
        { return AccountErrors.EmailTaken; }
        finally
        {
            if (!committed && !commitAttempted)
                foreach (var key in written)
                {
                    try { storage.DeleteCreatedEvidence(key); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { logger.LogError("Uncommitted clinic intake evidence cleanup failed with {ExceptionType}.", e.GetType().Name); throw; }
                }
            else if (!committed) logger.LogError("Clinic intake commit outcome is uncertain; private evidence retained for reconciliation.");
        }
        return new ClinicApplicantReceipt(user.Id, request.Id, submission.Id, request.Status.ToString(), !user.EmailConfirmed,
            user.EmailConfirmed ? "NotRequired" : await email.SendAsync(user, ct));
    }
    private async Task<bool> LockAsync(string resource, CancellationToken ct)
    {
        var codes = await db.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(ct);
        ct.ThrowIfCancellationRequested(); if (codes.Single() < -3) throw new InvalidOperationException("Unexpected clinic intake lock failure."); return codes.Single() >= 0;
    }
    private static Result<ClinicApplicantReceipt> Failure(IEnumerable<IdentityError> errors) => Result<ClinicApplicantReceipt>.Failure(errors.Select(e =>
        e.Code is "DuplicateEmail" or "DuplicateUserName" ? AccountErrors.EmailTaken : Error.Validation($"Account.{e.Code}", e.Description, e.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email")));
}
