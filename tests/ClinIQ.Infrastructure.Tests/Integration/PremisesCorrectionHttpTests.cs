using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.PremisesCorrections;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.PremisesCorrections;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PremisesCorrectionHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpEnforcesWebCsrfOwnershipReviewAndPrivateEvidenceForBothPlaceTypes()
    {
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var setup = new PremisesCorrectionTests(fixture);
        var (owner, place) = await setup.Place(); var (managerId, _, branch) = await setup.Branch();
        var patient = await fixture.UserAsync("Patient"); var stranger = await PracticeLocationTestData.ApprovedDoctor(fixture);
        await using var db = fixture.Database.CreateContext();
        var ownerEmail = await db.Users.Where(x => x.Id == owner).Select(x => x.Email).SingleAsync();
        var managerEmail = await db.Users.Where(x => x.Id == managerId).Select(x => x.Email).SingleAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "premises-corrections-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "200";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        const string own = "/api/premises-corrections", review = "/api/admin/premises-corrections";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var anonymous = Client(false); using var doctor = Client(true); using var manager = Client(true); using var admin = Client(true); using var other = Client(true); using var mobile = Client(false);
            await HttpSessionTestSupport.WebLoginAsync(doctor, ownerEmail!); await HttpSessionTestSupport.WebLoginAsync(manager, managerEmail!);
            await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!); await HttpSessionTestSupport.WebLoginAsync(other, stranger.Email);
            using (var response = await anonymous.GetAsync(own)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous corrections return private 401");
            using (var response = await anonymous.GetAsync(review)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous review returns private 401");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email = patient.Email, password = DoctorReviewFixture.Password }))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                mobile.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString());
            }
            using (var response = await mobile.GetAsync(own)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient mobile cannot access corrections");
            using (var response = await doctor.GetAsync(review)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Doctor cannot access Admin queue");
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var form = Upload(Details(place) with { Name = "CSRF attempt" }, place.Version))
            using (var response = await doctor.PostAsync($"{own}/locations/{place.Id}", form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Correction submission requires Web CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(doctor));
            using (var form = Upload(Details(place), place.Version))
            using (var response = await doctor.PostAsync($"{own}/locations/{place.Id}", form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "No-op correction is rejected");
            using (var form = Upload(Details(place) with { Name = "Invalid file" }, place.Version, validFile: false))
            using (var response = await doctor.PostAsync($"{own}/locations/{place.Id}", form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Fake PDF is rejected");
            var correction = await Submit(doctor, "locations", place.Id, Details(place) with { Address = "Corrected HTTP address" }, place.Version);
            Check(correction.Status == "PendingReview" && correction.ApplicantUserId == owner, "Applicant and pending state are derived by the server");
            using (var response = await anonymous.GetAsync($"/api/doctors/me/practice-locations/{place.Id}")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Underlying place remains private");
            var stillApproved = (await doctor.GetFromJsonAsync<PracticeLocationDetail>($"/api/doctors/me/practice-locations/{place.Id}"))!.Location;
            Check(stillApproved == place, "Pending correction leaves approved data and version untouched");
            using (var form = Upload(Details(place) with { Name = "Duplicate pending" }, place.Version))
            using (var response = await doctor.PostAsync($"{own}/locations/{place.Id}", form)) Check(response.StatusCode == HttpStatusCode.Conflict, "One pending correction per place");
            using (var response = await other.GetAsync($"{own}/{correction.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Foreign doctor cannot read correction");
            var document = Assert.Single(correction.Documents);
            using (var response = await other.GetAsync($"{own}/{correction.Id}/documents/{document.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Foreign doctor cannot download evidence");
            foreach (var (client, prefix) in new[] { (doctor, own), (admin, review) })
            {
                using var response = await client.GetAsync($"{prefix}/{correction.Id}/documents/{document.Id}");
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response) && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(DoctorReviewFixture.Evidence), "Applicant and Admin privately download verified evidence");
            }
            using (var response = await admin.PostAsJsonAsync($"{review}/{correction.Id}/approve", Decision(correction, same: false))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Approval requires same-premises attestation");
            using (var response = await admin.PostAsJsonAsync($"{review}/{correction.Id}/request-information", Decision(correction))) Check(response.StatusCode == HttpStatusCode.OK, "Admin can request information with reason");
            var second = await Submit(doctor, "locations", place.Id, correction.Proposed with { Name = "Corrected HTTP place" }, place.Version);
            using (var response = await admin.PostAsJsonAsync($"{review}/{second.Id}/approve", Decision(second, version: Convert.ToBase64String(new byte[8])))) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale correction version cannot approve");
            using (var response = await admin.PostAsJsonAsync($"{review}/{second.Id}/approve", Decision(second))) Check(response.StatusCode == HttpStatusCode.OK, "Verified same-premises correction approves");
            using (var response = await admin.PostAsJsonAsync($"{review}/{second.Id}/reject", Decision(second))) Check(response.StatusCode == HttpStatusCode.Conflict, "Final decision cannot be reversed");
            var after = (await doctor.GetFromJsonAsync<PracticeLocationDetail>($"/api/doctors/me/practice-locations/{place.Id}"))!.Location;
            Check(after.Address == second.Proposed.Address && after.ConsultationFee == place.ConsultationFee && after.CurrentSubmissionId == place.CurrentSubmissionId && after.Status == "Approved", "Only physical details change; fee, approval and source remain");
            var history = (await doctor.GetFromJsonAsync<PremisesCorrectionPage>($"{own}?locationId={place.Id}"))!;
            Check(history.TotalCount == 2 && history.Items.Any(x => x.Status == "NeedsInformation"), "All immutable attempts remain in own history");
            var branchCorrection = await Submit(manager, "branches", branch.Id, PremisesDetails.From(branch) with { PublicPhone = "01099999999" }, Convert.ToBase64String(branch.Version));
            using (var response = await admin.PostAsJsonAsync($"{review}/{branchCorrection.Id}/approve", Decision(branchCorrection))) Check(response.StatusCode == HttpStatusCode.OK, "Current manager can correct their approved branch through review");
            await db.Entry(branch).ReloadAsync(); Check(branch.PublicPhone == "01099999999", "Branch correction persists after HTTP approval");
            using (var form = Upload(PremisesDetails.From(branch) with { Name = "Unauthorized branch" }, Convert.ToBase64String(branch.Version)))
            using (var response = await doctor.PostAsync($"{own}/branches/{branch.Id}", form)) Check(response.StatusCode == HttpStatusCode.NotFound, "Doctor role does not grant branch management");
            using (var response = await admin.GetAsync($"{review}?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Queue paging is bounded");
            using (var response = await anonymous.GetAsync("/openapi/v1.json"))
            { var schema = await response.Content.ReadAsStringAsync(); Check(response.IsSuccessStatusCode && schema.Contains("/api/premises-corrections/locations/{targetId}", StringComparison.Ordinal) && schema.Contains("SamePremisesVerified", StringComparison.OrdinalIgnoreCase), "OpenAPI exposes typed correction contracts"); }
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"PREMISES_CORRECTION_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<PremisesCorrectionDto> Submit(HttpClient client, string scope, Guid id, PremisesDetails details, string version)
            {
                using var form = Upload(details, version); using var response = await client.PostAsync($"{own}/{scope}/{id}", form);
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response) && response.Headers.Location is not null, "Multipart correction returns private 201 and Location");
                return (await response.Content.ReadFromJsonAsync<PremisesCorrectionDto>())!;
            }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
    private static PremisesDetails Details(PracticeLocationDto p) => new(p.Name, p.Address, p.Latitude, p.Longitude);
    private static object Decision(PremisesCorrectionDto r, bool same = true, string? version = null) => new { reason = "Verified correction at the same premises", version = version ?? r.Version, samePremisesVerified = same, licenseVerified = true, authorityVerified = true };
    private static MultipartFormDataContent Upload(PremisesDetails p, string version, bool validFile = true)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in new[] { ("Name", p.Name), ("Address", p.Address), ("Latitude", p.Latitude?.ToString(CultureInfo.InvariantCulture)), ("Longitude", p.Longitude?.ToString(CultureInfo.InvariantCulture)),
            ("PublicPhone", p.PublicPhone), ("LicensingAuthority", p.LicensingAuthority), ("LicenseNumber", p.LicenseNumber), ("Reason", "Correct a typo at the same premises"), ("TargetVersion", version), ("ApplicantUserId", Guid.NewGuid().ToString()), ("Status", "Approved"), ("ConsultationFee", "9999") })
            if (value is not null) form.Add(new StringContent(value), name);
        var file = new ByteArrayContent(validFile ? DoctorReviewFixture.Evidence : Encoding.UTF8.GetBytes("not a PDF")); file.Headers.ContentType = new("application/pdf");
        form.Add(file, "FacilityLicense", "synthetic.pdf"); return form;
    }
}
