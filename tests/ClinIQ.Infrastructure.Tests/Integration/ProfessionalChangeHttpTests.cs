using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.DoctorProfessionalChanges;
using ClinIQ.Application.Features.DoctorReviews;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class ProfessionalChangeHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpPreservesActiveProfilesAndProtectsProfessionalChangesAndEvidence()
    {
        var owner = await ApprovedDoctor(); var other = await ApprovedDoctor(); var patient = await fixture.UserAsync("Patient");
        var specialty = Specialty.Create("تخصص تغيير", "Synthetic change specialty").Value;
        await using (var setup = fixture.Database.CreateContext()) { setup.Specialties.Add(specialty); await setup.SaveChangesAsync(); }
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "professional-change-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts); var apiRoot = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(apiRoot, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection;
        start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot; start.Environment["Auth__Sessions__PermitLimit"] = "100";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            lock (log) log.AppendLine(args.Data); const string marker = "Now listening on: ";
            if (args.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(args.Data[(args.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim()));
        };
        process.ErrorDataReceived += (_, args) => { lock (log) log.AppendLine(args.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>();
        void Check(bool valid, string label) { Assert.True(valid, label); checks.Add(label); }
        const string ownPath = "/api/doctors/me/professional-changes", profilePath = "/api/doctors/me/professional-profile", adminPath = "/api/admin/doctor-professional-changes";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var anonymous = Client(false); using var doctor = Client(true); using var stranger = Client(true); using var admin = Client(true); using var ordinary = Client(false);
            using (var response = await anonymous.GetAsync(profilePath)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous professional profile is no-store 401");
            using (var response = await anonymous.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous change queue is no-store 401");
            ordinary.DefaultRequestHeaders.Authorization = new("Bearer", await MobileLogin(patient.Email!));
            using (var response = await ordinary.GetAsync(profilePath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot read a doctor's professional profile");
            using (var response = await ordinary.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot review professional changes");
            await WebLogin(doctor, owner.Email); await WebLogin(stranger, other.Email);
            await HttpSessionTestSupport.WebLoginAsync(admin,fixture.Admin.Email!);
            var before = (await doctor.GetFromJsonAsync<VerifiedDoctorProfileDto>(profilePath))!;
            var otherBefore = (await stranger.GetFromJsonAsync<VerifiedDoctorProfileDto>(profilePath))!;
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var body = Upload(before, "Changed without CSRF", specialty.Id))
            using (var response = await doctor.PostAsync(ownPath, body)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Cookie change submission requires CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(doctor));
            using (var body = Upload(before with { Version = "AAAAAAAAAAA=" }, "Stale proposal", specialty.Id))
            using (var response = await doctor.PostAsync(ownPath, body)) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale approved-profile version cannot submit");
            using (var body = Upload(before, "Missing supporting evidence", specialty.Id, evidence: false))
            using (var response = await doctor.PostAsync(ownPath, body)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Changed professional fields require supporting evidence");
            ProfessionalChangeSummary submitted;
            using (var body = Upload(before, "Changed verified doctor", specialty.Id))
            using (var response = await doctor.PostAsync(ownPath, body))
            {
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response), "Doctor submits a private pending change with 201");
                submitted = (await response.Content.ReadFromJsonAsync<ProfessionalChangeSummary>())!;
                Check(submitted.UserId == owner.UserId && submitted.DoctorProfileId == before.Id && submitted.Status == "PendingReview", "Server derives doctor ownership and initial pending state");
                Check(response.Headers.Location?.ToString().EndsWith(submitted.Id.ToString(), StringComparison.Ordinal) == true, "201 includes the own-request GET location");
            }
            Check(before == await doctor.GetFromJsonAsync<VerifiedDoctorProfileDto>(profilePath), "Pending proposal leaves approved fields and profile version unchanged");
            using (var response = await doctor.GetAsync("/api/auth/me")) Check(response.StatusCode == HttpStatusCode.OK, "Pending change does not disable an approved session");
            using (var body = Upload(before, "Second concurrent pending", specialty.Id))
            using (var response = await doctor.PostAsync(ownPath, body)) Check(response.StatusCode == HttpStatusCode.Conflict, "A second pending request is rejected");
            ProfessionalChangeSummary otherRequest;
            using (var body = Upload(otherBefore, "Other proposed doctor", otherBefore.SpecialtyId))
            using (var response = await stranger.PostAsync(ownPath, body))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode); otherRequest = (await response.Content.ReadFromJsonAsync<ProfessionalChangeSummary>())!;
            }
            using (var response = await doctor.GetAsync(ownPath))
            {
                var page = (await response.Content.ReadFromJsonAsync<ProfessionalChangePage>())!;
                Check(response.StatusCode == HttpStatusCode.OK && page.TotalCount == 1 && page.Items.All(row => row.UserId == owner.UserId), "Own list never includes another doctor's request");
            }
            using (var response = await stranger.GetAsync($"{ownPath}/{submitted.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Another doctor cannot read request details");
            using (var response = await admin.GetAsync(adminPath + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Admin pagination is bounded");
            using (var response = await admin.GetAsync(adminPath))
            {
                var page = (await response.Content.ReadFromJsonAsync<ProfessionalChangePage>())!;
                Check(response.StatusCode == HttpStatusCode.OK && page.TotalCount == 2 && NoStore(response), "Admin queue defaults to pending requests");
            }
            ProfessionalChangeDetail detail;
            using (var response = await doctor.GetAsync($"{ownPath}/{submitted.Id}"))
            {
                var text = await response.Content.ReadAsStringAsync(); detail = JsonSerializer.Deserialize<ProfessionalChangeDetail>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Check(!text.Contains("storageKey", StringComparison.OrdinalIgnoreCase) && !text.Contains("sha256", StringComparison.OrdinalIgnoreCase), "Details expose no storage keys or internal hashes");
                Check(detail.Current == before && detail.Request.ProfessionalName == "Changed verified doctor", "Details separate the active profile from the proposed version");
            }
            var documentId = detail.Documents[0].Id;
            using (var response = await doctor.GetAsync($"{ownPath}/{submitted.Id}/documents/{documentId}"))
            {
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(DoctorReviewFixture.Evidence), "Doctor downloads their own verified synthetic bytes");
                Check(NoStore(response) && response.Content.Headers.ContentDisposition?.DispositionType == "attachment" && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "Private downloads are no-store nosniff attachments");
            }
            using (var response = await stranger.GetAsync($"{ownPath}/{submitted.Id}/documents/{documentId}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Another doctor cannot download the evidence");
            using (var response = await doctor.GetAsync($"{ownPath}/{Guid.NewGuid()}/documents/{documentId}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Document identity cannot be used under a different parent");
            using (var response = await admin.GetAsync($"{adminPath}/{submitted.Id}/documents/{documentId}")) Check(response.StatusCode == HttpStatusCode.OK, "Admin downloads review evidence through a protected route");
            using (var response = await doctor.PostAsJsonAsync($"{adminPath}/{submitted.Id}/approve", new { reason = "Forged", version = submitted.Version })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Doctor cannot approve via the Admin route");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{submitted.Id}/approve", new { reason = "", version = submitted.Version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "A review reason is mandatory");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{submitted.Id}/approve", new { reason = "Reviewed", version = "AAAAAAAAAAA=" })) Check(response.StatusCode == HttpStatusCode.Conflict, "A stale review version is rejected");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{submitted.Id}/approve", new
            {
                reason = "Synthetic professional evidence reviewed", version = submitted.Version,
                reviewerUserId = owner.UserId, reviewedAtUtc = "2099-01-01T00:00:00Z", professionalName = "Forged decision name", specialtyId = before.SpecialtyId, role = "Admin"
            }))
            {
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Admin approves the stored proposal atomically");
                var text = await response.Content.ReadAsStringAsync();
                Check(!response.Headers.Contains("Set-Cookie") && !text.Contains("accessToken", StringComparison.OrdinalIgnoreCase), "Approval issues no login credentials");
            }
            var after = (await doctor.GetFromJsonAsync<VerifiedDoctorProfileDto>(profilePath))!;
            Check(after.Id == before.Id && after.UserId == before.UserId && after.Version != before.Version, "Approval preserves identity and publishes a new profile version");
            Check(after.ProfessionalName == submitted.ProfessionalName && after.SpecialtyId == specialty.Id && after.ProfessionalGrade == ProfessionalGrade.Consultant, "Only stored proposed professional fields become active");
            var reviewed = (await admin.GetFromJsonAsync<ProfessionalChangeDetail>($"{adminPath}/{submitted.Id}"))!;
            Check(reviewed.ReviewerUserId == fixture.Admin.Id && reviewed.OriginalProfessionalName == before.ProfessionalName && reviewed.Request.Status == "Approved", "Trusted reviewer and old approved values remain in history");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{submitted.Id}/reject", new { reason = "Late reversal", version = submitted.Version })) Check(response.StatusCode == HttpStatusCode.Conflict, "Final decision cannot be reversed");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{otherRequest.Id}/reject", new { reason = "Not supported", version = otherRequest.Version })) Check(response.StatusCode == HttpStatusCode.OK, "Admin can reject with a reason");
            Check(otherBefore == await stranger.GetFromJsonAsync<VerifiedDoctorProfileDto>(profilePath), "Rejection preserves the previously approved profile and version");
            using (var response = await admin.GetAsync(adminPath))
            {
                var page = (await response.Content.ReadFromJsonAsync<ProfessionalChangePage>())!; Check(page.TotalCount == 0, "Reviewed requests leave the default pending queue");
            }
            using (var response = await anonymous.GetAsync("/openapi/v1.json"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("paths").TryGetProperty(adminPath + "/{id}/approve", out _), "OpenAPI documents the protected professional review operation");
            }
            await using var db = fixture.Database.CreateContext();
            Check(await db.ProfessionalChangeEvidenceAccesses.CountAsync(access => access.DocumentId == documentId) == 2, "SQL audits only permitted evidence access grants");
            Check((await db.DoctorRegistrations.SingleAsync(row => row.UserId == owner.UserId)).Status == DoctorRegistrationStatus.Approved, "Initial professional registration stays approved");
            Check((await db.DoctorRegistrations.SingleAsync(row => row.UserId == owner.UserId)).ProfessionalName != after.ProfessionalName, "Initial submission remains a historical snapshot");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"PROFESSIONAL_CHANGE_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<string> MobileLogin(string email)
            {
                using var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!;
            }
            async Task<string> Csrf(HttpClient client)
            {
                using var response = await client.GetAsync("/api/auth/web/csrf"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("requestToken").GetString()!;
            }
            async Task WebLogin(HttpClient client, string email)
            {
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(client));
                using var response = await client.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(client));
            }
            MultipartFormDataContent Upload(VerifiedDoctorProfileDto profile, string name, Guid targetSpecialty, bool evidence = true)
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(name), "ProfessionalName"); form.Add(new StringContent(targetSpecialty.ToString()), "SpecialtyId");
                form.Add(new StringContent(((int)ProfessionalGrade.Consultant).ToString(System.Globalization.CultureInfo.InvariantCulture)), "ProfessionalGrade");
                form.Add(new StringContent(profile.Version), "ProfileVersion");
                form.Add(new StringContent(fixture.Admin.Id.ToString()), "UserId"); form.Add(new StringContent(Guid.NewGuid().ToString()), "DoctorProfileId");
                form.Add(new StringContent("Approved"), "Status");
                if (evidence)
                    foreach (var field in new[] { "PracticeLicense", "SyndicateRegistration", "IdentityEvidence", "ProfessionalGradeEvidence" })
                    {
                        var file = new ByteArrayContent(DoctorReviewFixture.Evidence); file.Headers.ContentType = new("application/pdf"); form.Add(file, field, "synthetic.pdf");
                    }
                return form;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
            lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }

        async Task<(Guid UserId, string Email)> ApprovedDoctor()
        {
            var doctor = await fixture.DoctorAsync();
            await fixture.AsAsync(fixture.Admin.Id, async provider =>
            {
                var reviews = provider.GetRequiredService<IDoctorReviewService>(); var detail = (await reviews.GetAsync(doctor.Receipt.RegistrationId, default)).Value;
                Assert.True((await reviews.ReviewAsync(detail.Registration.Id, true, "Synthetic approved profile", detail.Version, default)).IsSuccess); return true;
            });
            return (doctor.Receipt.UserId, doctor.Email);
        }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
