using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinIQ.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PasswordRecoveryHttpTests(SqlServerIntegrationFixture database, ITestOutputHelper output)
    : IClassFixture<SqlServerIntegrationFixture>
{
    [SqlServerFact]
    public async Task RealHttpRecoversPasswordWithoutEnumerationAndInvalidatesOldSessions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        var repository = directory!.FullName;
        var apiRoot = Path.Combine(repository, "src", "ClinIQ.API");
        var artifacts = Path.Combine(repository, ".artifacts", "password-recovery-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var privateRoot = Path.Combine(artifacts, "private");
        await using var setup = database.CreateContext();
        var connection = new SqlConnectionStringBuilder(setup.Database.GetConnectionString()) { Pooling = true }.ConnectionString;
        var checks = new List<string>();
        void Check(bool condition, string label) { Assert.True(condition, label); checks.Add(label); }
        const string email = "synthetic-recovery@example.test", unconfirmedEmail = "unconfirmed-recovery@example.test";
        const string oldPassword = "Synthetic-Old1!", newPassword = "Synthetic-New2!";
        const string forgotPath = "/api/auth/forgot-password", resetPath = "/api/auth/reset-password";
        Guid userId;

        var host = await StartHost(100, "api");
        try
        {
            using var client = Client(host.Address);
            using (var response = await client.PostAsJsonAsync(forgotPath, new { email = "invalid" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed forgot input returns 400");
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId = Guid.Empty, token = "", newPassword = "" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Missing reset proof fields return 400");
            using (var response = await client.PostAsJsonAsync("/api/auth/patients/register", new { fullName = "Synthetic Recovery Patient", email, password = oldPassword }))
            {
                Check(response.StatusCode == HttpStatusCode.Created, "Recovery fixture registers through the real API");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                userId = json.RootElement.GetProperty("userId").GetGuid();
            }
            using (var response = await client.PostAsJsonAsync("/api/auth/patients/register", new { fullName = "Unconfirmed Synthetic Patient", email = unconfirmedEmail, password = oldPassword }))
                Check(response.StatusCode == HttpStatusCode.Created, "Unconfirmed comparison account is created");
            foreach (var address in new[] { "unknown@example.test", email, unconfirmedEmail })
            {
                using var response = await client.PostAsJsonAsync(forgotPath, new { email = address });
                Check(response.StatusCode == HttpStatusCode.Accepted && (await response.Content.ReadAsStringAsync()).Length == 0 &&
                    !response.Headers.Contains("Set-Cookie"), "Unknown and unconfirmed requests share empty 202 without credentials");
            }
            Check(!Directory.Exists(Path.Combine(privateRoot, "password-reset-emails")), "Unconfirmed and unknown accounts receive no reset message");
            var confirmationLink = await LinkFor("development-emails", "ConfirmationUrl", email);
            var confirmationQuery = System.Web.HttpUtility.ParseQueryString(confirmationLink.Query);
            using (var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new { userId, token = confirmationQuery["token"] }))
                Check(response.StatusCode == HttpStatusCode.NoContent, "Patient email is confirmed through the real API");
            string access, refresh;
            using (var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = oldPassword }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Old password creates the pre-reset session");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                access = json.RootElement.GetProperty("accessToken").GetString()!;
                refresh = json.RootElement.GetProperty("refreshToken").GetString()!;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, forgotPath))
            {
                request.Headers.Host = "attacker.invalid";
                request.Content = JsonContent.Create(new { email });
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Accepted && (await response.Content.ReadAsStringAsync()).Length == 0 &&
                    !response.Headers.Contains("Set-Cookie"), "Confirmed account uses the same public 202 shape");
                Check(response.Headers.CacheControl?.NoStore == true, "Recovery responses are no-store");
            }
            var resetLink = await LinkFor("password-reset-emails", "PasswordResetUrl", email);
            Check(resetLink.Host == "localhost" && resetLink.Port == 5173 && resetLink.AbsolutePath == "/reset-password",
                "Reset link uses trusted configuration instead of the supplied Host header");
            var resetQuery = System.Web.HttpUtility.ParseQueryString(resetLink.Query);
            Check(Guid.Parse(resetQuery["userId"]!) == userId, "Recovery message is bound to the intended account");
            var proof = resetQuery["token"]!;
            using (var response = await client.PostAsJsonAsync(forgotPath, new { email = email.ToUpperInvariant() }))
                Check(response.StatusCode == HttpStatusCode.Accepted, "Cooldown remains publicly accepted");
            Check(Directory.GetFiles(Path.Combine(privateRoot, "password-reset-emails"), "*.json").Length == 1,
                "Cooldown prevents a second message for the same account");
            client.DefaultRequestHeaders.Authorization = new("Bearer", access);
            using (var response = await client.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.OK, "Forgot request alone does not invalidate the session");
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId, token = "invalid!", newPassword }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid reset proof returns 400");
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId = Guid.NewGuid(), token = proof, newPassword }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "A valid proof cannot reset another account");
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId, token = proof, newPassword = "weak" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Password policy still applies during recovery");
            using (var response = await client.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.OK, "Rejected reset leaves the existing session usable");
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId, token = proof, newPassword }))
                Check(response.StatusCode == HttpStatusCode.NoContent && !response.Headers.Contains("Set-Cookie") &&
                    (await response.Content.ReadAsStringAsync()).Length == 0, "Successful reset returns 204 without automatic login credentials");
            using (var response = await client.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Reset invalidates the old access credential");
            using (var response = await client.PostAsJsonAsync("/api/auth/mobile/refresh", new { refreshToken = refresh }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Reset invalidates the old refresh credential");
            client.DefaultRequestHeaders.Authorization = null;
            using (var response = await client.PostAsJsonAsync(resetPath, new { userId, token = proof, newPassword = "Synthetic-Replay3!" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Successful reset proof cannot be replayed");
            using (var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = oldPassword }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Old password cannot log in after recovery");
            using (var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = newPassword }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "New password can log in");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Patient" }),
                    "Recovery does not grant a new role");
            }
            var noticeFiles = Directory.GetFiles(Path.Combine(privateRoot, "password-reset-notifications"), "*.json");
            Check(noticeFiles.Length == 1, "Successful reset produces a development security notification");
            var notice = await File.ReadAllTextAsync(noticeFiles.Single());
            Check(!notice.Contains(oldPassword, StringComparison.Ordinal) && !notice.Contains(newPassword, StringComparison.Ordinal),
                "Notification includes no password");
            using (var response = await client.GetAsync("/openapi/v1.json"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("paths").GetProperty(forgotPath).GetProperty("post").GetProperty("responses").TryGetProperty("202", out _) &&
                    json.RootElement.GetProperty("paths").GetProperty(resetPath).GetProperty("post").GetProperty("responses").TryGetProperty("204", out _),
                    "OpenAPI documents both recovery endpoints");
            }
        }
        finally { await StopHost(host, "api"); }

        var limited = await StartHost(2, "limited-api");
        try
        {
            using var client = Client(limited.Address);
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.PostAsJsonAsync(forgotPath, new { email = "unknown@example.test" });
                Check(response.StatusCode == HttpStatusCode.Accepted, "Requests below the recovery IP limit are accepted");
            }
            using var rejected = await client.PostAsJsonAsync(forgotPath, new { email = "unknown@example.test" });
            Check(rejected.StatusCode == HttpStatusCode.TooManyRequests, "The recovery IP limit returns 429");
        }
        finally { await StopHost(limited, "limited-api"); }

        await using var reader = database.CreateContext();
        var confirmed = await reader.Users.SingleAsync(x => x.Id == userId);
        Check(confirmed.EmailConfirmed, "SQL email confirmation state is preserved");
        Check(await reader.UserTokens.CountAsync(x => x.UserId == userId) == 0, "Successful reset clears its cooldown metadata");
        Check(await reader.AuthSessions.CountAsync(x => x.UserId == userId && x.RevokedAtUtc != null) == 1,
            "SQL records revocation of the pre-reset session");
        await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
        output.WriteLine($"PASSWORD_RECOVERY_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

        HttpClient Client(Uri uri) => new(new HttpClientHandler { UseCookies = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(45) };
        async Task<Uri> LinkFor(string folder, string property, string recipient)
        {
            foreach (var path in Directory.GetFiles(Path.Combine(privateRoot, folder), "*.json"))
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                if (json.RootElement.GetProperty("To").GetString() == recipient)
                    return new Uri(json.RootElement.GetProperty(property).GetString()!);
            }
            throw new InvalidOperationException("Expected synthetic development email was not found.");
        }
        async Task<(Process Process, Uri Address, System.Text.StringBuilder Log)> StartHost(int limit, string prefix)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(apiRoot, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ConnectionStrings__DefaultConnection"] = connection;
            start.Environment["Auth__PrivateStorageRoot"] = privateRoot;
            start.Environment["Auth__PasswordResetUrl"] = "http://localhost:5173/reset-password";
            start.Environment["Auth__RateLimit__PermitLimit"] = "100";
            start.Environment["Auth__Sessions__PermitLimit"] = "100";
            start.Environment["Auth__Recovery__PermitLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var process = new Process { StartInfo = start };
            var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                lock (log) log.AppendLine(args.Data);
                const string marker = "Now listening on: ";
                if (args.Data?.Contains(marker, StringComparison.Ordinal) == true)
                    address.TrySetResult(args.Data[(args.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim());
            };
            process.ErrorDataReceived += (_, args) => { lock (log) log.AppendLine(args.Data); };
            try
            {
                Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                return (process, new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(30))), log);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                lock (log) File.WriteAllText(Path.Combine(artifacts, prefix + ".log"), log.ToString());
                process.Dispose(); throw;
            }
        }
        async Task StopHost((Process Process, Uri Address, System.Text.StringBuilder Log) state, string prefix)
        {
            if (!state.Process.HasExited) state.Process.Kill(entireProcessTree: true);
            await state.Process.WaitForExitAsync();
            lock (state.Log) File.WriteAllText(Path.Combine(artifacts, prefix + ".log"), state.Log.ToString());
            state.Process.Dispose();
        }
    }
}
