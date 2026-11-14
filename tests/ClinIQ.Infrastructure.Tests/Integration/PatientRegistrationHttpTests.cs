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
public sealed class PatientRegistrationHttpTests(SqlServerIntegrationFixture database, ITestOutputHelper output)
    : IClassFixture<SqlServerIntegrationFixture>
{
    [SqlServerFact]
    public async Task RealHttpRegistersPatientConfirmsEmailAndAuthenticatesMobileOnly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        var repository = directory!.FullName;
        var apiRoot = Path.Combine(repository, "src", "ClinIQ.API");
        var artifacts = Path.Combine(repository, ".artifacts", "patient-registration-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var privateRoot = Path.Combine(artifacts, "private");
        await using var setup = database.CreateContext();
        var connection = new SqlConnectionStringBuilder(setup.Database.GetConnectionString()) { Pooling = true }.ConnectionString;
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
        start.Environment["Auth__RateLimit__PermitLimit"] = "100";
        start.Environment["Auth__Sessions__PermitLimit"] = "100";
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
        const string email = "synthetic-patient@example.test", password = "Synthetic-Test1!";
        try
        {
            var uri = new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            using var mobile = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
            using var web = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
            const string register = "/api/auth/patients/register";
            foreach (var (body, label) in new (object, string)[]
            {
                (new { email, password }, "Missing name is rejected"),
                (new { fullName = "   ", email, password }, "Whitespace name is rejected"),
                (new { fullName = new string('N', 201), email, password }, "Overlong name is rejected"),
                (new { fullName = "Synthetic Patient", email = "invalid", password }, "Invalid email is rejected"),
                (new { fullName = "Synthetic Patient", email, password = "weak" }, "Weak password is rejected"),
                (new { fullName = "Synthetic Patient", email, password = new string('P', 129) }, "Overlong password is rejected")
            })
            {
                using var response = await mobile.PostAsJsonAsync(register, body);
                Check(response.StatusCode == HttpStatusCode.BadRequest, label);
            }
            var forgedId = Guid.NewGuid();
            Guid userId;
            using (var response = await mobile.PostAsJsonAsync(register, new
            {
                fullName = "  Synthetic Patient  ", email = "  " + email + "  ", password,
                role = "Admin", roles = new[] { "Admin", "Doctor" }, userId = forgedId, emailConfirmed = true
            }))
            {
                Check(response.StatusCode == HttpStatusCode.Created, "Registration returns 201");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                userId = json.RootElement.GetProperty("userId").GetGuid();
                Check(userId != Guid.Empty && userId != forgedId, "User ID is generated by the server");
                Check(json.RootElement.GetProperty("emailConfirmationRequired").GetBoolean(), "Confirmation remains required");
                Check(json.RootElement.GetProperty("emailDeliveryStatus").GetString() == "DevelopmentFile", "Development delivery is explicit");
                Check(!response.Headers.Contains("Set-Cookie") && !json.RootElement.TryGetProperty("accessToken", out _) &&
                    !json.RootElement.TryGetProperty("refreshToken", out _), "Registration issues no session credentials");
                Check(response.Headers.CacheControl?.NoStore == true, "Registration response is no-store");
            }
            using (var response = await mobile.PostAsJsonAsync(register, new { fullName = "Changed Name", email = email.ToUpperInvariant(), password }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "Case-insensitive duplicate email returns 409");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = "Wrong1!" }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Wrong password returns 401");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email, password }))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.Forbidden && json.RootElement.GetProperty("title").GetString() == "Auth.EmailUnconfirmed", "Unconfirmed patient cannot log in");
            }
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Registration alone does not authenticate Me");
            using (var response = await mobile.GetAsync("/api/patients/me/profile"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                    "Anonymous profile reads return private 401 responses");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/resend-confirmation", new { email }))
                Check(response.StatusCode == HttpStatusCode.Accepted, "Patient can request confirmation resend");
            var mailFile = Directory.GetFiles(Path.Combine(privateRoot, "development-emails"), "*.json").First();
            using var mail = JsonDocument.Parse(await File.ReadAllTextAsync(mailFile));
            var confirmation = new Uri(mail.RootElement.GetProperty("ConfirmationUrl").GetString()!);
            var query = System.Web.HttpUtility.ParseQueryString(confirmation.Query);
            using (var response = await mobile.PostAsJsonAsync("/api/auth/confirm-email", new { userId, token = query["token"] }))
                Check(response.StatusCode == HttpStatusCode.NoContent, "Patient email confirmation returns 204");
            using (var response = await web.GetAsync("/api/auth/web/csrf"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("requestToken").GetString()!);
            }
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email, password }))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.Forbidden && json.RootElement.GetProperty("title").GetString() == "Auth.ClientNotAllowed", "Patient role does not allow web login");
            }
            string access, refresh;
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email, password }))
            {
                Check(response.StatusCode == HttpStatusCode.OK && !response.Headers.Contains("Set-Cookie"), "Confirmed patient receives mobile credentials without cookies");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Patient" }), "Only Patient authority is granted");
                access = json.RootElement.GetProperty("accessToken").GetString()!;
                refresh = json.RootElement.GetProperty("refreshToken").GetString()!;
            }
            mobile.DefaultRequestHeaders.Authorization = new("Bearer", access);
            using (var response = await mobile.GetAsync("/api/auth/me"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("user").GetProperty("id").GetGuid() == userId,
                    "Bearer Me returns the newly registered patient");
            }
            const string profilePath = "/api/patients/me/profile";
            using (var response = await mobile.GetAsync(profilePath))
                Check(response.StatusCode == HttpStatusCode.NotFound, "Registration does not create an incomplete profile");
            using (var response = await mobile.PutAsJsonAsync(profilePath, new { fullName = "Patient", phoneNumber = "not-a-phone" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Profile contact validation returns 400");
            Guid profileId; string profileVersion;
            using (var response = await mobile.PutAsJsonAsync(profilePath, new
            {
                fullName = "  Patient booking contact  ", phoneNumber = "  +201012345678  ",
                userId = forgedId, id = forgedId, role = "Admin", createdByUserId = forgedId, phoneNumberConfirmed = true
            }))
            {
                Check(response.StatusCode == HttpStatusCode.Created, "First profile save returns 201");
                Check(response.Headers.Location?.ToString().EndsWith(profilePath, StringComparison.Ordinal) == true, "Created profile includes its own GET location");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                profileId = json.RootElement.GetProperty("id").GetGuid();
                profileVersion = json.RootElement.GetProperty("version").GetString()!;
                Check(profileId != forgedId && json.RootElement.GetProperty("userId").GetGuid() == userId, "Profile ownership and ID come from the server");
                Check(json.RootElement.GetProperty("fullName").GetString() == "Patient booking contact" &&
                    json.RootElement.GetProperty("phoneNumber").GetString() == "+201012345678", "Patient contact is trimmed");
                Check(response.Headers.CacheControl?.NoStore == true, "Profile response is no-store");
            }
            using (var response = await mobile.PutAsJsonAsync(profilePath, new { fullName = "Changed", phoneNumber = "01012345678" }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "Existing profile requires the current version");
            using (var response = await mobile.PutAsJsonAsync(profilePath, new { fullName = "Changed", phoneNumber = "01012345678", version = "malformed" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed profile version returns 400");
            using (var response = await mobile.PutAsJsonAsync(profilePath, new { fullName = "Changed", phoneNumber = "01012345678", version = profileVersion }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Matching version updates the existing profile");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("id").GetGuid() == profileId && json.RootElement.GetProperty("version").GetString() != profileVersion,
                    "Update preserves PatientId and changes the version");
            }
            using (var response = await mobile.PutAsJsonAsync(profilePath, new { fullName = "Stale overwrite", phoneNumber = "01012345678", version = profileVersion }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "A stale profile update cannot overwrite contact");
            using (var response = await mobile.GetAsync(profilePath))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("fullName").GetString() == "Changed", "GET returns only the saved own profile");
            }
            using (var response = await mobile.GetAsync($"/api/patients/{forgedId}/profile"))
                Check(response.StatusCode == HttpStatusCode.NotFound, "No arbitrary-user profile route is exposed");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/refresh", new { refreshToken = refresh }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Patient session refresh succeeds");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                access = json.RootElement.GetProperty("accessToken").GetString()!;
                refresh = json.RootElement.GetProperty("refreshToken").GetString()!;
            }
            mobile.DefaultRequestHeaders.Authorization = new("Bearer", access);
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/logout", new { refreshToken = refresh }))
                Check(response.StatusCode == HttpStatusCode.NoContent, "Patient logout succeeds");
            using (var response = await mobile.GetAsync("/api/auth/me"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Logout invalidates patient access");
            using (var response = await mobile.GetAsync("/openapi/v1.json"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("paths").GetProperty(register).GetProperty("post").GetProperty("responses").TryGetProperty("201", out _),
                    "Patient registration is documented in OpenAPI");
            }
            await using var reader = database.CreateContext();
            var user = await reader.Users.SingleAsync();
            Check(user.Id == userId && user.FullName == "Synthetic Patient" && user.EmailConfirmed, "SQL stores trimmed patient data and confirmed email");
            Check((await reader.Roles.SingleAsync()).Name == "Patient" && await reader.UserRoles.CountAsync() == 1,
                "Injected roles create no Admin or Doctor authority in SQL");
            Check(await reader.DoctorRegistrations.CountAsync() == 0, "Patient signup creates no doctor application");
            var profile = await reader.PatientProfiles.SingleAsync();
            Check(profile.Id == profileId && profile.UserId == userId && profile.CreatedByUserId == userId && profile.LastModifiedByUserId == userId,
                "SQL preserves trusted patient ownership and auditing");
            Check(!user.PhoneNumberConfirmed, "Saving contact does not claim verified phone ownership");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"PATIENT_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
        }
        finally
        {
            if (!api.HasExited) api.Kill(entireProcessTree: true);
            await api.WaitForExitAsync();
            lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }
    }
}
