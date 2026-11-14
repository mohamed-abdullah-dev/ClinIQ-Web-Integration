using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.DoctorResubmissions;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Doctors;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorResubmissionHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RejectedDoctorCanCorrectOnlyOwnRegistrationWithoutLoginAndStillWaitsForApproval()
    {
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var owner = await fixture.DoctorAsync(); var other = await fixture.DoctorAsync();
        await ResubmissionSupport.Review(fixture, owner.Receipt.RegistrationId, false);
        var directory = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "doctor-resubmission-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var api = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = api, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(api, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__RateLimit__PermitLimit"] = "100";
        var log = new StringBuilder(); var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: ";
            if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) ready.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); string? token = null;
        void Check(bool condition, string label) { Assert.True(condition, label); checks.Add(label); }
        const string path = "/api/auth/doctors/resubmission";
        try
        {
            var address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            using (var response = await client.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Missing proof is a private 401");
            foreach (var email in new[] { "missing@example.test", other.Email, owner.Email })
            {
                using var response = await client.PostAsJsonAsync(path + "/link", new { email });
                Check(response.StatusCode == HttpStatusCode.Accepted && NoStore(response) && !response.Headers.Contains("Set-Cookie") && (await response.Content.ReadAsByteArrayAsync()).Length == 0,
                    "Valid link requests have indistinguishable empty no-store 202 and create no login cookie");
            }
            token = ResubmissionSupport.ReadLink(fixture.PrivateRoot, owner.Email);
            client.DefaultRequestHeaders.Add("X-Doctor-Resubmission", token);
            using (var response = await client.GetAsync("/api/auth/me")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "A resubmission header is not a session");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using (var response = await client.GetAsync("/api/admin/doctor-registrations")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "A resubmission secret is not an Admin bearer token");
            client.DefaultRequestHeaders.Authorization = null;
            DoctorResubmissionPreview preview;
            using (var response = await client.GetAsync(path))
            {
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response) && !response.Headers.Contains("Set-Cookie"), "Valid proof previews only its rejected registration without login");
                preview = (await response.Content.ReadFromJsonAsync<DoctorResubmissionPreview>())!;
                Check(preview.RegistrationId == owner.Receipt.RegistrationId && preview.RejectionReason == "Synthetic review decision" && preview.Documents.Length == 3, "Preview includes own rejected fields and reason");
            }
            using (var response = await client.GetAsync(path + "/documents/" + preview.Documents[0].Id))
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response) && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(DoctorReviewFixture.Evidence), "Private own evidence downloads through the same scoped proof");
            await using (var db = fixture.Database.CreateContext())
            {
                var foreign = await db.Set<DoctorEvidenceDocument>().FirstAsync(x => x.DoctorRegistrationId == other.Receipt.RegistrationId);
                using var response = await client.GetAsync(path + "/documents/" + foreign.Id); Check(response.StatusCode == HttpStatusCode.NotFound, "Proof cannot download another doctor's evidence");
            }
            using (var form = Form(false))
            using (var response = await client.PostAsync(path, form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Correction requires fresh supporting evidence");
            DoctorResubmissionReceipt receipt;
            using (var form = Form(true))
            using (var response = await client.PostAsync(path, form))
            {
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response) && !response.Headers.Contains("Set-Cookie"), "Valid corrected multipart creates a pending attempt with 201 and no session cookie");
                receipt = (await response.Content.ReadFromJsonAsync<DoctorResubmissionReceipt>())!;
                Check(receipt.PreviousRegistrationId == owner.Receipt.RegistrationId && receipt.AttemptNumber == 2 && receipt.Status == "PendingReview", "Server assigns lineage, attempt, and pending state despite forged form fields");
            }
            using (var response = await client.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Consumed proof cannot preview");
            using (var form = Form(true))
            using (var response = await client.PostAsync(path, form)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Consumed proof cannot submit again");
            using (var response = await client.GetAsync(path + "/documents/" + preview.Documents[0].Id)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Consumed proof cannot download evidence");
            await using (var db = fixture.Database.CreateContext())
            {
                Check(!await db.AuthSessions.AnyAsync(x => x.UserId == owner.Receipt.UserId), "Correction creates no auth session");
                Check(!await db.UserRoles.AnyAsync(x => x.UserId == owner.Receipt.UserId), "Correction grants no role");
                Check(!await db.DoctorProfiles.AnyAsync(x => x.UserId == owner.Receipt.UserId), "Correction creates no approved profile");
                Check((await db.DoctorRegistrations.SingleAsync(x => x.Id == receipt.RegistrationId)).UserId == owner.Receipt.UserId, "Forged UserId cannot change account ownership");
            }
            using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            using (var response = await browser.GetAsync("/api/auth/web/csrf"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("requestToken").GetString());
            }
            using (var response = await browser.PostAsJsonAsync("/api/auth/web/login", new { email = owner.Email, password = DoctorReviewFixture.Password }))
            {
                Check(response.StatusCode == HttpStatusCode.Forbidden && !response.Headers.Contains("Set-Cookie") && (await response.Content.ReadAsStringAsync()).Contains("Auth.DoctorPendingReview", StringComparison.Ordinal), "Correct credentials still receive under-review 403 without a login session");
            }
            await ResubmissionSupport.Review(fixture, receipt.RegistrationId, true);
            using (var response = await browser.PostAsJsonAsync("/api/auth/web/login", new { email = owner.Email, password = DoctorReviewFixture.Password }))
                Check(response.StatusCode == HttpStatusCode.OK && response.Headers.Contains("Set-Cookie"), "Latest approval permits normal confirmed doctor login despite historical rejection");
            using (var response = await browser.GetAsync("/api/doctors/me/professional-profile"))
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("HTTP Corrected Doctor", StringComparison.Ordinal), "Approved profile uses the corrected professional fields");
            using (var response = await client.GetAsync("/openapi/v1.json"))
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("X-Doctor-Resubmission", StringComparison.Ordinal), "OpenAPI documents the explicit scoped header");
            lock (log) Check(!log.ToString().Contains(token, StringComparison.Ordinal), "API logs do not contain the resubmission bearer secret");
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"DOCTOR_RESUBMISSION_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{directory}");

            MultipartFormDataContent Form(bool evidence)
            {
                var form = new MultipartFormDataContent(); form.Add(new StringContent("HTTP Corrected Doctor"), "ProfessionalName");
                form.Add(new StringContent("01012345678"), "PhoneNumber"); form.Add(new StringContent("CORRECTED"), "RegistrationNumber");
                form.Add(new StringContent(fixture.SpecialtyId.ToString()), "SpecialtyId"); form.Add(new StringContent("1"), "ProfessionalGrade");
                form.Add(new StringContent(other.Receipt.UserId.ToString()), "UserId"); form.Add(new StringContent("Approved"), "Status"); form.Add(new StringContent("99"), "AttemptNumber");
                if (evidence) foreach (var field in new[] { "PracticeLicense", "SyndicateRegistration", "IdentityEvidence" })
                { var file = new ByteArrayContent(DoctorReviewFixture.Evidence); file.Headers.ContentType = new("application/pdf"); form.Add(file, field, "synthetic.pdf"); }
                return form;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
            lock (log) File.WriteAllText(Path.Combine(directory, "api.log"), log.ToString());
        }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
