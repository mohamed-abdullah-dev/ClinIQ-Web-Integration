using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorReviewHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpEnforcesAdminPrivacyCsrfDecisionsAndDoctorLogin()
    {
        var approved = await fixture.DoctorAsync(false); var rejected = await fixture.DoctorAsync();
        var patient = await fixture.UserAsync("Patient");
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "doctor-review-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var apiRoot = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(apiRoot, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection;
        start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "100";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            lock (log) log.AppendLine(args.Data);
            const string marker = "Now listening on: ";
            if (args.Data?.Contains(marker, StringComparison.Ordinal) == true)
                listening.TrySetResult(new Uri(args.Data[(args.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim()));
        };
        process.ErrorDataReceived += (_, args) => { lock (log) log.AppendLine(args.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>();
        void Check(bool condition, string name) { Assert.True(condition, name); checks.Add(name); }
        const string path = "/api/admin/doctor-registrations";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(40));
            using var anonymous = Client(false); using var admin = Client(true); using var ordinary = Client(false);
            using var web = Client(true); using var doctorWeb = Client(true);
            using (var response = await anonymous.GetAsync("/openapi/v1.json"))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "OpenAPI generation succeeds with the optional review-state filter");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("paths").TryGetProperty(path + "/{id}/approve", out _), "OpenAPI includes the protected approval operation");
            }
            using (var response = await anonymous.GetAsync(path))
            { Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous admin list returns 401"); Check(NoStore(response), "Even unauthorized review responses are no-store"); }
            await HttpSessionTestSupport.WebLoginAsync(admin,fixture.Admin.Email!);
            ordinary.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await MobileLogin(anonymous, patient.Email!));
            using (var response = await ordinary.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot read the review queue");
            using (var response = await admin.GetAsync(path + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Pagination is validated");
            using (var response = await admin.GetAsync(path))
            {
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Admin gets a private paginated queue");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("totalCount").GetInt32() == 2, "Queue defaults to both pending requests");
            }
            var id = approved.Receipt.RegistrationId;
            using var detailsResponse = await admin.GetAsync($"{path}/{id}");
            var detailsText = await detailsResponse.Content.ReadAsStringAsync(); using var details = JsonDocument.Parse(detailsText);
            Check(detailsResponse.StatusCode == HttpStatusCode.OK && NoStore(detailsResponse), "Admin gets protected request details");
            Check(!detailsText.Contains("storageKey", StringComparison.OrdinalIgnoreCase) && !detailsText.Contains("passwordHash", StringComparison.OrdinalIgnoreCase), "Details expose no storage keys or password hashes");
            var version = details.RootElement.GetProperty("version").GetString()!;
            var documentId = details.RootElement.GetProperty("documents")[0].GetProperty("id").GetGuid();
            var downloadPath = $"{path}/{id}/documents/{documentId}";
            using (var response = await anonymous.GetAsync(downloadPath)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous evidence download returns 401");
            using (var response = await ordinary.GetAsync(downloadPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient evidence download returns 403");
            using (var response = await admin.GetAsync($"{path}/{Guid.NewGuid()}/documents/{documentId}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Document cannot be downloaded under a different parent");
            using (var response = await admin.GetAsync(downloadPath))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Admin downloads a reviewed document");
                Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(DoctorReviewFixture.Evidence), "Downloaded bytes match stored synthetic evidence");
                Check(response.Content.Headers.ContentType?.MediaType == "application/pdf" && response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Evidence is an attachment with its validated media type");
                Check(NoStore(response) && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "Evidence disables storage and MIME sniffing");
            }
            using (var response = await ordinary.PostAsJsonAsync($"{path}/{id}/approve", new { reason = "Forged", version })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot approve a registration");
            using (var response = await admin.PostAsJsonAsync($"{path}/{id}/approve", new { reason = "", version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Approval requires a reason");
            using (var response = await admin.PostAsJsonAsync($"{path}/{id}/approve", new { reason = "Reviewed", version = "invalid" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed versions return 400");
            using (var response = await admin.PostAsJsonAsync($"{path}/{id}/approve", new { reason = "Reviewed", version = Convert.ToBase64String(new byte[8]) })) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale review versions return 409");
            using (var response = await admin.PostAsJsonAsync($"{path}/{id}/approve", new
            {
                reason = "Synthetic evidence approved", version, reviewerUserId = approved.Receipt.UserId,
                reviewedAtUtc = "2099-01-01T00:00:00Z", status = "Rejected", professionalName = "Forged name"
            }))
            {
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Admin approval succeeds without cacheable response");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("status").GetString() == "Approved" && json.RootElement.GetProperty("doctorProfileId").GetGuid() != Guid.Empty, "Approval returns the new profile and final state");
            }
            using (var response = await admin.PostAsJsonAsync($"{path}/{id}/reject", new { reason = "Late opposite decision", version })) Check(response.StatusCode == HttpStatusCode.Conflict, "Final decision cannot be overwritten");
            var doctorCsrf = await Csrf(doctorWeb);
            doctorWeb.DefaultRequestHeaders.Add("X-CSRF-TOKEN", doctorCsrf);
            using (var response = await doctorWeb.PostAsJsonAsync("/api/auth/web/login", new { email = approved.Email, password = DoctorReviewFixture.Password }))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.Forbidden && json.RootElement.GetProperty("title").GetString() == "Auth.EmailUnconfirmed", "Approval alone does not permit login before email confirmation");
            }
            await fixture.ConfirmAsync(approved.Receipt.UserId);
            using (var response = await doctorWeb.PostAsJsonAsync("/api/auth/web/login", new { email = approved.Email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.OK, "Approved confirmed doctor logs in through Web");
            using (var response = await doctorWeb.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Approved doctor cannot read Admin review records");
            web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(web));
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email = fixture.Admin.Email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.OK, "Admin web login succeeds");
            web.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using var rejectedDetailsResponse = await admin.GetAsync($"{path}/{rejected.Receipt.RegistrationId}");
            Check(rejectedDetailsResponse.StatusCode == HttpStatusCode.OK, "Admin can load the second pending request before rejection");
            using var rejectedDetails = JsonDocument.Parse(await rejectedDetailsResponse.Content.ReadAsStringAsync());
            var rejectedVersion = rejectedDetails.RootElement.GetProperty("version").GetString()!;
            using (var response = await web.PostAsJsonAsync($"{path}/{rejected.Receipt.RegistrationId}/reject", new { reason = "Synthetic evidence rejected", version = rejectedVersion })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Admin cookie review writes require CSRF");
            web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(web));
            using (var response = await web.PostAsJsonAsync($"{path}/{rejected.Receipt.RegistrationId}/reject", new { reason = "Synthetic evidence rejected", version = rejectedVersion })) Check(response.StatusCode == HttpStatusCode.OK, "Admin web rejection succeeds with fresh CSRF proof");
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email = rejected.Email, password = DoctorReviewFixture.Password }))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.Forbidden && json.RootElement.GetProperty("title").GetString() == "Auth.DoctorRejected", "Rejected doctor receives the clear rejection code after password verification");
            }
            await using (var db = fixture.Database.CreateContext())
            {
                var reviews = await db.DoctorRegistrationReviews.ToArrayAsync();
                Check(reviews.Length == 2 && reviews.All(row => row.ReviewerUserId == fixture.Admin.Id && row.ReviewedAtUtc.Year != 2099), "SQL records server-authenticated reviewer and actual review time");
                var profile = await db.DoctorProfiles.SingleAsync();
                Check(profile.UserId == approved.Receipt.UserId && profile.ProfessionalName == "Synthetic Doctor" && profile.CreatedByUserId == fixture.Admin.Id, "Verified profile uses submitted data and trusted actor");
                Check(await db.DoctorEvidenceAccesses.CountAsync(row => row.ViewerUserId == fixture.Admin.Id) == 1, "Only successful authorized evidence grants are audited");
            }
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Check((await users.RemoveFromRoleAsync((await users.FindByIdAsync(fixture.Admin.Id.ToString()))!, "Admin")).Succeeded, "Synthetic Admin role can be removed for live authorization check");
            }
            using (var response = await admin.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Existing credentials lose access immediately after Admin role removal");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"DOCTOR_REVIEW_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<string> MobileLogin(HttpClient client, string email)
            {
                using var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password });
                Check(response.StatusCode == HttpStatusCode.OK, "Synthetic eligible account gets a mobile session");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!;
            }
            async Task<string> Csrf(HttpClient client)
            {
                using var response = await client.GetAsync("/api/auth/web/csrf");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("requestToken").GetString()!;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
