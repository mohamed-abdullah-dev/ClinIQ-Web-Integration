using ClinIQ.Application.Features.Accounts;
using ClinIQ.Infrastructure.Accounts;

namespace ClinIQ.API.Infrastructure;

internal static class AdminBootstrapConsole
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        // Passwords must never arrive in command-line arguments, config, or redirected input.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Admin bootstrap requires an interactive terminal. No account was created.");
            return 1;
        }
        Console.WriteLine("Create the first Development Admin. Email confirmation is required before login.");
        Console.Write("Full name: ");
        var name = Console.ReadLine();
        Console.Write("Email: ");
        var email = Console.ReadLine();
        if (name is null || email is null) return 1;
        var password = ReadPassword("Password (hidden; max 128 characters; Esc cancels): ");
        if (password is null) return 1;
        var confirmation = ReadPassword("Repeat password: ");
        if (confirmation is null) return 1;
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match. No account was created.");
            return 1;
        }
        try
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<FirstAdminBootstrapper>()
                .BootstrapAsync(new AdminBootstrapInput(name, email, password));
            return result.Match(receipt =>
            {
                Console.WriteLine($"ADMIN_BOOTSTRAP: created; userId={receipt.UserId}; emailDelivery={receipt.EmailDeliveryStatus}");
                Console.WriteLine("Confirm the email using the existing confirmation flow. No session was issued.");
                return 0;
            }, errors =>
            {
                foreach (var error in errors) Console.Error.WriteLine($"{error.Code}: {error.Description}");
                return 1;
            });
        }
        catch (Exception error)
        {
            // An unexpected commit failure may have an ambiguous outcome. Do not print SQL or credentials.
            Console.Error.WriteLine($"Admin bootstrap failed ({error.GetType().Name}). Inspect database state before retrying; no success was confirmed.");
            return 1;
        }
    }

    private static string? ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var buffer = new char[128];
        var length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(buffer, 0, length); }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0) buffer[--length] = '\0';
                    continue;
                }
                if (char.IsControl(key.KeyChar)) continue;
                if (length == buffer.Length)
                {
                    Console.WriteLine();
                    Console.Error.WriteLine("Password exceeds 128 characters. Cancelled; no account was created.");
                    return null;
                }
                buffer[length++] = key.KeyChar;
            }
        }
        finally { Array.Clear(buffer); }
    }
}
