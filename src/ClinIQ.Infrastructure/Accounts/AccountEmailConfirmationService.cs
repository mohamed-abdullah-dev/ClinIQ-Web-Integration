using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinIQ.Infrastructure.Accounts;

public sealed class AccountEmailConfirmationService(UserManager<AppUser> users, RegistrationStorage storage,
    IHostEnvironment environment, IOptions<EmailConfirmationOptions> options, ILogger<AccountEmailConfirmationService> logger)
    : IEmailConfirmationService
{
    // Resolve validated settings before a registration service can write an account, including direct DI callers.
    private readonly string? _confirmationUrl = options.Value.Url;

    public async Task<string> SendAsync(AppUser user, CancellationToken cancellationToken)
    {
        // This local adapter deliberately does not claim delivery to a real mailbox.
        if (!environment.IsDevelopment()) return "Unavailable";
        var directory = Path.Combine(storage.Root, "development-emails");
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        var createdHere = false;
        try
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = QueryHelpers.AddQueryString(_confirmationUrl!, new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(), ["token"] = encoded
            });
            Directory.CreateDirectory(directory);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            createdHere = true;
            await JsonSerializer.SerializeAsync(output, new { To = user.Email, Subject = "Confirm your ClinIQ email", ConfirmationUrl = link },
                cancellationToken: cancellationToken);
            return "DevelopmentFile";
        }
        catch (Exception error)
        {
            // Registration has already committed. Report delivery failure explicitly; resend remains available.
            logger.LogWarning("Confirmation email delivery failed with {ExceptionType}.", error.GetType().Name);
            if (createdHere)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Partial confirmation email cleanup failed with {ExceptionType}.", cleanup.GetType().Name);
                }
            }
            return "Unavailable";
        }
    }

    public async Task<Result<Success>> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string decoded;
        try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return AccountErrors.InvalidConfirmation; }
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return AccountErrors.InvalidConfirmation;
        if (user.EmailConfirmed)
        {
            // Validate even on a repeated request: knowing a user ID is not proof of ownership.
            if (!await users.VerifyUserTokenAsync(user, users.Options.Tokens.EmailConfirmationTokenProvider, "EmailConfirmation", decoded))
                return AccountErrors.InvalidConfirmation;
            return Result.Success;
        }
        var result = await users.ConfirmEmailAsync(user, decoded);
        return result.Succeeded ? Result.Success : AccountErrors.InvalidConfirmation;
    }

    public async Task ResendAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null && !user.EmailConfirmed)
            await SendAsync(user, cancellationToken);
        // Same public response for unknown, already-confirmed and existing accounts.
    }
}
