using System.Globalization;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class AccountPasswordRecoveryService(AppDbContext context, UserManager<AppUser> users,
    RegistrationStorage storage, IHostEnvironment environment, IConfiguration configuration,
    TimeProvider clock, ILogger<AccountPasswordRecoveryService> logger) : IPasswordRecoveryService
{
    private const string ThrottleProvider = "ClinIQ.PasswordRecovery";
    private const string ThrottleName = "LastEmailAtUtc";
    private readonly Uri _resetUrl = ResolveResetUrl(configuration, environment);

    public async Task RequestAsync(string email, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureFreshContext();
            var user = await users.FindByEmailAsync(email.Trim());
            if (user is null || !user.EmailConfirmed) return;
            string token;
            await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
            {
                if (!await LockAsync(user.Id, cancellationToken)) return;
                await context.Entry(user).ReloadAsync(cancellationToken);
                if (context.Entry(user).State == EntityState.Detached || !user.EmailConfirmed) return;
                var entry = await context.UserTokens.SingleOrDefaultAsync(x => x.UserId == user.Id &&
                    x.LoginProvider == ThrottleProvider && x.Name == ThrottleName, cancellationToken);
                var now = clock.GetUtcNow();
                if (entry is not null)
                {
                    if (!DateTimeOffset.TryParseExact(entry.Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
                        throw new InvalidOperationException("Password recovery cooldown metadata is invalid.");
                    if (now < last.AddMinutes(1)) return;
                }
                else
                {
                    entry = new IdentityUserToken<Guid> { UserId = user.Id, LoginProvider = ThrottleProvider, Name = ThrottleName };
                    context.UserTokens.Add(entry);
                }
                entry.Value = now.ToString("O", CultureInfo.InvariantCulture);
                token = await users.GeneratePasswordResetTokenAsync(user);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = QueryHelpers.AddQueryString(_resetUrl.AbsoluteUri, new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(), ["token"] = encoded
            });
            await WriteDevelopmentEmailAsync("password-reset-emails",
                new { To = user.Email, Subject = "Reset your ClinIQ password", PasswordResetUrl = link }, cancellationToken);
        }
        finally
        {
            // Mitigate the normal known/unknown-account timing difference. This is not a constant-time guarantee.
            var remaining = TimeSpan.FromMilliseconds(500) - clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
                await Task.Delay(remaining, clock, cancellationToken);
        }
    }

    public async Task<Result<Success>> ResetAsync(Guid userId, string token, string newPassword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureFreshContext();
        string decoded;
        try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return PasswordRecoveryErrors.InvalidToken; }
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.EmailConfirmed || !await users.VerifyUserTokenAsync(user,
            users.Options.Tokens.PasswordResetTokenProvider, UserManager<AppUser>.ResetPasswordTokenPurpose, decoded))
            return PasswordRecoveryErrors.InvalidToken;

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await LockAsync(userId, cancellationToken)) return PasswordRecoveryErrors.Busy;
            // A second reset may have completed while this request waited for the per-user lock.
            await context.Entry(user).ReloadAsync(cancellationToken);
            if (context.Entry(user).State == EntityState.Detached || !user.EmailConfirmed)
                return PasswordRecoveryErrors.InvalidToken;
            var reset = await users.ResetPasswordAsync(user, decoded, newPassword);
            if (!reset.Succeeded)
            {
                if (reset.Errors.Any(error => !error.Code.StartsWith("Password", StringComparison.Ordinal)))
                    return PasswordRecoveryErrors.InvalidToken;
                return Result<Success>.Failure(reset.Errors.Select(error =>
                    Error.Validation($"Account.{error.Code}", error.Description, "NewPassword")));
            }
            if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded ||
                !(await users.SetLockoutEndDateAsync(user, null)).Succeeded)
                throw new InvalidOperationException("Password recovery could not clear login lockout.");
            var now = clock.GetUtcNow();
            await context.AuthSessions.Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, now), cancellationToken);
            await context.UserTokens.Where(x => x.UserId == userId && x.LoginProvider == ThrottleProvider && x.Name == ThrottleName)
                .ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // Native Identity changed the security stamp as well, invalidating every older reset proof and session.
        await WriteDevelopmentEmailAsync("password-reset-notifications",
            new { To = user.Email, Subject = "Your ClinIQ password was changed" }, cancellationToken);
        return Result.Success;
    }

    private void EnsureFreshContext()
    {
        if (context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Password recovery requires a context without pending changes.");
    }

    private async Task<bool> LockAsync(Guid userId, CancellationToken cancellationToken)
    {
        var resource = $"ClinIQ.AccountCredentials.{userId:N}";
        var codes = await context.Database.SqlQuery<int>($"""
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=5000;
            SELECT @code AS [Value];
            """).ToArrayAsync(cancellationToken);
        var code = codes.Single();
        if (code < -3) throw new InvalidOperationException("Unexpected password recovery lock failure.");
        cancellationToken.ThrowIfCancellationRequested();
        return code >= 0;
    }

    private async Task WriteDevelopmentEmailAsync(string folder, object payload, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogWarning("Password recovery email delivery is unavailable in this environment.");
            return;
        }
        var path = Path.Combine(storage.Root, folder, $"{Guid.NewGuid():N}.json");
        var createdHere = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            createdHere = true;
            await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogWarning("Password recovery email delivery failed with {ExceptionType}.", error.GetType().Name);
            if (createdHere)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Partial recovery email cleanup failed with {ExceptionType}.", cleanup.GetType().Name);
                }
            }
        }
    }

    private static Uri ResolveResetUrl(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Auth:PasswordResetUrl"] ??
            (environment.IsDevelopment() ? "http://localhost:5173/reset-password" : null);
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            (!environment.IsDevelopment() && uri.Scheme != "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Configure a trusted absolute Auth:PasswordResetUrl; HTTPS is required outside Development, without credentials, query, or fragment.");
        return uri;
    }
}
