using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinIQ.Application;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SessionHttpTests(SqlServerIntegrationFixture database, ITestOutputHelper output) : IClassFixture<SqlServerIntegrationFixture>
{
    [SqlServerFact]
    public async Task RealHttpEnforcesCookiesCsrfBearerRotationLogoutAndMe()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ClinIQ.sln"))) repository = repository.Parent;
        var root = repository!.FullName;
        var artifacts = Path.Combine(root, ".artifacts", "session-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        await using var context = database.CreateContext();
        var connection = new SqlConnectionStringBuilder(context.Database.GetConnectionString()) { Pooling = true }.ConnectionString;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = Path.Combine(artifacts, "private")
        }).Build();
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "src", "ClinIQ.API") };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication(); services.AddInfrastructure(config);
        const string email = "synthetic-http-admin@example.test", password = "Synthetic-Test1!";
        Guid userId;
        await using (var provider = services.BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded);
            var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, FullName = "Synthetic HTTP Admin" };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Patient"))).Succeeded);
            Assert.True((await users.AddToRoleAsync(user,"Patient")).Succeeded);
            userId = user.Id;
        }
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = environment.ContentRootPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(environment.ContentRootPath, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = connection;
        start.Environment["Auth__PrivateStorageRoot"] = Path.Combine(artifacts, "private");
        start.Environment["Auth__Sessions__PermitLimit"] = "200";
        using var api = new Process { StartInfo = start };
        var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new System.Text.StringBuilder();
        api.OutputDataReceived += (_, args) =>
        {
            lock (log) log.AppendLine(args.Data);
            const string marker = "Now listening on: ";
            if (args.Data?.Contains(marker, StringComparison.Ordinal) == true)
                address.TrySetResult(args.Data[(args.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim());
        };
        api.ErrorDataReceived += (_, args) => { lock (log) log.AppendLine(args.Data); };
        Assert.True(api.Start()); api.BeginOutputReadLine(); api.BeginErrorReadLine();
        var checks = new List<string>();
        void Check(bool condition, string label) { Assert.True(condition, label); checks.Add(label); }
        try
        {
            var uri = new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            using var web = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
            using var mobile = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
            async Task<string> Csrf()
            {
                using var response = await web.GetAsync("/api/auth/web/csrf");
                Check(response.StatusCode == HttpStatusCode.OK, "CSRF token endpoint succeeds");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var token = json.RootElement.GetProperty("requestToken").GetString()!;
                web.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
                return token;
            }
            using (var anonymous = await mobile.GetAsync("/api/auth/me"))
                Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && anonymous.Content.Headers.ContentType?.MediaType == "application/problem+json", "Anonymous Me returns JSON 401");
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email, password }))
                Check(response.StatusCode == HttpStatusCode.BadRequest && !response.Headers.Contains("Set-Cookie"), "Web login requires CSRF and sets no session on failure");
            var anonymousCsrf = await Csrf();
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email, password = "Wrong1!" }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized && !response.Headers.Contains("Set-Cookie"), "Wrong password has no session cookie");
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email, password, rememberMe = true, role = "Doctor" }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Web login succeeds");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(!json.RootElement.TryGetProperty("accessToken", out _) && !json.RootElement.TryGetProperty("refreshToken", out _), "Web JSON excludes credentials");
                var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
                Check(cookies.Length == 2 && cookies.All(x => x.Contains("httponly", StringComparison.OrdinalIgnoreCase) && x.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)), "Both credentials use HttpOnly SameSite Strict cookies");
                Check(cookies.All(x => x.Contains("expires=", StringComparison.OrdinalIgnoreCase)), "RememberMe sets bounded persistent cookies");
                Check(response.Headers.CacheControl?.NoStore == true, "Authentication response is no-store");
            }
            using (var response = await web.GetAsync("/api/auth/me"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("user").GetProperty("id").GetGuid() == userId, "Cookie authentication returns only current user");
                Check(json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Admin" }), "Request role does not grant Doctor");
            }
            using (var response = await web.PostAsync("/api/auth/web/refresh", null))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Anonymous CSRF token is rejected after login identity change");
            await Csrf();
            using (var response = await web.PostAsync("/api/auth/web/refresh", null))
                Check(response.StatusCode == HttpStatusCode.OK, "Web refresh rotates cookies");
            web.DefaultRequestHeaders.Add("Authorization", "Bearer invalid");
            using (var response = await web.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Invalid Authorization never falls back to a cookie");
            web.DefaultRequestHeaders.Remove("Authorization");
            using (var response = await web.PostAsync("/api/auth/web/logout", null))
                Check(response.StatusCode == HttpStatusCode.NoContent, "Web logout revokes server session");
            using (var response = await web.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Web Me fails after logout");
            string access, refresh;
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email, password }))
            {
                Check(response.StatusCode == HttpStatusCode.OK && !response.Headers.Contains("Set-Cookie"), "Mobile login returns credentials without cookies or CSRF");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                access = json.RootElement.GetProperty("accessToken").GetString()!;
                refresh = json.RootElement.GetProperty("refreshToken").GetString()!;
                Check(json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"Patient"}),"Mobile session exposes only Patient even when the account is also Admin");
            }
            mobile.DefaultRequestHeaders.Authorization = new("Bearer", access);
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.OK, "Mobile Bearer authenticates Me");
            string nextAccess, nextRefresh;
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/refresh", new { refreshToken = refresh }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Mobile refresh succeeds");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                nextAccess = json.RootElement.GetProperty("accessToken").GetString()!;
                nextRefresh = json.RootElement.GetProperty("refreshToken").GetString()!;
            }
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Rotated old mobile access is rejected");
            mobile.DefaultRequestHeaders.Authorization = new("Bearer", nextAccess);
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.OK, "New mobile access succeeds");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/refresh", new { refreshToken = refresh }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Replay returns 401");
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Replay also revokes new access");
            mobile.DefaultRequestHeaders.Authorization = null;
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/logout", new { refreshToken = nextRefresh }))
                Check(response.StatusCode == HttpStatusCode.NoContent, "Logout is idempotent after revocation");
            using var cors = new HttpRequestMessage(HttpMethod.Options, "/api/auth/web/login");
            cors.Headers.Add("Origin", "http://localhost:5173"); cors.Headers.Add("Access-Control-Request-Method", "POST");
            using (var response = await mobile.SendAsync(cors))
                Check(response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var values) && values.Contains("true"), "Configured origin supports credentialed CORS");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"SESSION_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
        }
        finally
        {
            if (!api.HasExited) api.Kill(entireProcessTree: true);
            await api.WaitForExitAsync();
            lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
