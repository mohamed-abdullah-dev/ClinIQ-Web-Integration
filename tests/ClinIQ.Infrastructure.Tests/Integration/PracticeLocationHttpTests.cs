using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.PracticeLocations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLocationHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpSeparatesLocationReviewFromDoctorApprovalAndProtectsDraftsEvidenceAndFees()
    {
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var owner = await PracticeLocationTestData.ApprovedDoctor(fixture); var other = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var patient = await fixture.UserAsync("Patient");
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "practice-location-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts); var api = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = api, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(api, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "100";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: ";
            if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool valid, string label) { Assert.True(valid, label); checks.Add(label); }
        const string own = "/api/doctors/me/practice-locations", adminPath = "/api/admin/practice-locations";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var anonymous = Client(false); using var doctor = Client(true); using var stranger = Client(true); using var admin = Client(true); using var ordinary = Client(false);
            foreach (var path in new[] { own, adminPath })
            { using var response = await anonymous.GetAsync(path); Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous access is a no-store 401"); }
            ordinary.DefaultRequestHeaders.Authorization = new("Bearer", await MobileLogin(patient.Email!));
            foreach (var path in new[] { own, adminPath })
            { using var response = await ordinary.GetAsync(path); Check(response.StatusCode == HttpStatusCode.Forbidden && NoStore(response), "Patient cannot manage or review practice locations"); }
            await WebLogin(doctor, owner.Email); await WebLogin(stranger, other.Email); await HttpSessionTestSupport.WebLoginAsync(admin,fixture.Admin.Email!);
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await doctor.PostAsJsonAsync(own, Draft(100))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Creating a location with web cookies requires CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(doctor));
            using (var response = await doctor.PostAsJsonAsync(own, new { name = "Missing price", address = "Address" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Omitted fee cannot silently become a free consultation");
            using (var response = await doctor.PostAsJsonAsync(own, new { name = "Foreign currency", address = "Address", consultationFee = 10, currency = "USD" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Only EGP is accepted");
            using (var response = await doctor.PostAsJsonAsync(own, Draft(-1))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Negative fees are rejected");
            using (var response = await doctor.PostAsJsonAsync(own, Draft(1.001m))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Fractional fees are rejected instead of silently rounded");
            PracticeLocationDto draft;
            using (var response = await doctor.PostAsJsonAsync(own, Draft(0)))
            {
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response), "An approved doctor creates a private draft with a zero EGP fee");
                draft = (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!;
                Check(draft.UserId == owner.UserId && draft.Status == "Draft" && draft.CurrentSubmissionId is null, "Server assigns owner and draft state despite forged fields");
                Check(response.Headers.Location?.ToString().EndsWith(draft.Id.ToString(), StringComparison.Ordinal) == true, "Draft 201 links to its own detail endpoint");
            }
            var second = await Created(doctor, 200); var foreign = await Created(stranger, 300);
            Check(second.Id != draft.Id && second.Address == draft.Address, "Equal addresses do not merge independent records");
            using (var response = await stranger.GetAsync(own + "/" + draft.Id)) Check(response.StatusCode == HttpStatusCode.NotFound, "Another doctor cannot read the private draft");
            var queue = (await admin.GetFromJsonAsync<PracticeLocationPage>(adminPath))!; Check(queue.TotalCount == 0, "Unsubmitted drafts are absent from the default Admin pending queue");
            using (var form = Upload(draft.Version, license: false))
            using (var response = await doctor.PostAsync(own + "/" + draft.Id + "/submissions", form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Work evidence does not replace the required facility license");
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var form = Upload(draft.Version))
            using (var response = await doctor.PostAsync(own + "/" + draft.Id + "/submissions", form)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Submitting evidence with cookies also requires CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(doctor));
            var pending = await Submitted(draft);
            queue = (await admin.GetFromJsonAsync<PracticeLocationPage>(adminPath))!; Check(queue.TotalCount == 1 && queue.Items[0].Id == pending.Id, "Submitted location enters the pending review queue");
            var first = (await doctor.GetFromJsonAsync<PracticeLocationDetail>(own + "/" + pending.Id))!.CurrentSubmission!;
            var doc = first.Documents.First();
            foreach (var client in new[] { doctor, admin })
            {
                var prefix = client == admin ? adminPath : own;
                using var response = await client.GetAsync($"{prefix}/{pending.Id}/submissions/{first.Id}/documents/{doc.Id}");
                Check(response.StatusCode == HttpStatusCode.OK && NoStore(response) && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff" &&
                    (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(DoctorReviewFixture.Evidence), "Owner and Admin can download integrity-checked private evidence");
            }
            using (var response = await stranger.GetAsync($"{own}/{pending.Id}/submissions/{first.Id}/documents/{doc.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Another doctor's evidence is hidden with 404");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{pending.Id}/submissions/{first.Id}/approve", new { reason = "Unchecked", version = pending.Version }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Approval requires explicit license/address and doctor-work verification");
            PracticeLocationDto needs;
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{pending.Id}/submissions/{first.Id}/request-information", new { reason = "Clarify the address", version = pending.Version }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Admin can request information with a reason"); needs = (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!;
                Check(needs.Status == "NeedsInformation", "Request for information does not activate the location");
            }
            using (var response = await doctor.PutAsJsonAsync(own + "/" + needs.Id + "/draft", Edit(needs, draft.Version))) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale draft edits are rejected");
            PracticeLocationDto edited;
            using (var response = await doctor.PutAsJsonAsync(own + "/" + needs.Id + "/draft", Edit(needs, needs.Version)))
            { Check(response.StatusCode == HttpStatusCode.OK, "Owner corrects the information-requested draft"); edited = (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!; }
            var resubmitted = await Submitted(edited); Check(resubmitted.AttemptNumber == 2 && resubmitted.CurrentSubmissionId != first.Id, "Resubmission creates a second immutable review snapshot");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{needs.Id}/submissions/{resubmitted.CurrentSubmissionId}/approve", new
            { reason = "Verified facility and work link independently", version = resubmitted.Version, licenseMatchesAddress = true, doctorWorkLinkVerified = true }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Admin approves the corrected location after both manual checks"); needs = (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!;
                Check(needs.Status == "Approved", "Successful review records approved state without granting management powers");
            }
            var history = (await doctor.GetFromJsonAsync<PracticeLocationSubmissionPage>($"{own}/{needs.Id}/submissions"))!;
            Check(history.TotalCount == 2 && history.Items[0].Status == "NeedsInformation" && history.Items[0].ReviewReason == "Clarify the address" && history.Items[1].Status == "Approved", "Submission history retains the old reason and the final approval");
            using (var response = await admin.PostAsJsonAsync($"{adminPath}/{needs.Id}/submissions/{first.Id}/approve", new
            { reason = "Late decision", version = needs.Version, licenseMatchesAddress = true, doctorWorkLinkVerified = true }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "A historical submission cannot replace the current decision");
            PracticeLocationDto price;
            using (var response = await doctor.PutAsJsonAsync(own + "/" + needs.Id + "/fee", new { consultationFee = 175, currency = "EGP", version = needs.Version, address = "Forged move" }))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Owner changes an approved location's fee without a new review"); price = (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!;
                Check(price.ConsultationFee == 175 && price.Address == "Corrected HTTP address" && price.Status == "Approved", "Fee update preserves the approved premises and approval state");
            }
            using (var response = await doctor.PutAsJsonAsync(own + "/" + needs.Id + "/fee", new { consultationFee = 99, currency = "EGP", version = needs.Version }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "A stale fee update cannot overwrite the new price");
            using (var response = await doctor.PutAsJsonAsync(own + "/" + needs.Id + "/draft", Edit(needs, price.Version))) Check(response.StatusCode == HttpStatusCode.Conflict, "The draft endpoint cannot silently relocate an approved practice");
            Check((await doctor.GetFromJsonAsync<PracticeLocationDetail>(own + "/" + second.Id))!.Location.ConsultationFee == 200, "A fee change at one location leaves another location's fee unchanged");
            var ownList = (await doctor.GetFromJsonAsync<PracticeLocationPage>(own))!; Check(ownList.TotalCount == 2 && ownList.Items.All(x => x.UserId == owner.UserId), "Doctor list includes only the doctor's own places");
            var all = (await admin.GetFromJsonAsync<PracticeLocationPage>(adminPath + "?status="))!; Check(all.TotalCount == 3, "Admin can explicitly include all draft and reviewed states");
            using (var response = await admin.GetAsync(adminPath + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Review queue pagination is bounded");
            using (var response = await anonymous.GetAsync("/openapi/v1.json"))
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("/api/admin/practice-locations", StringComparison.Ordinal), "OpenAPI publishes the location review contract");
            await using (var db = fixture.Database.CreateContext())
            {
                Check(await db.PracticeLocationEvidenceAccesses.CountAsync(x => x.DocumentId == doc.Id) == 2, "Private owner/Admin downloads have access-grant audits");
                Check(!await (from role in db.UserRoles join name in db.Roles on role.RoleId equals name.Id where role.UserId == owner.UserId && name.NormalizedName == "CLINICMANAGER" select role).AnyAsync(), "Location approval does not grant a clinic-manager role");
            }
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"PRACTICE_LOCATION_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<string> MobileLogin(string email)
            { using var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!; }
            async Task<string> Csrf(HttpClient client)
            { using var response = await client.GetAsync("/api/auth/web/csrf"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("requestToken").GetString()!; }
            async Task WebLogin(HttpClient client, string email)
            { client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(client)); using var response = await client.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf(client)); }
            object Draft(decimal fee) => new { name = "HTTP place", address = "HTTP address", consultationFee = fee, currency = "EGP", userId = patient.Id, status = "Approved", doctorProfileId = Guid.NewGuid() };
            object Edit(PracticeLocationDto row, string version) => new { name = row.Name, address = "Corrected HTTP address", consultationFee = 120, currency = "EGP", version };
            async Task<PracticeLocationDto> Created(HttpClient client, decimal fee)
            { using var response = await client.PostAsJsonAsync(own, Draft(fee)); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!; }
            async Task<PracticeLocationDto> Submitted(PracticeLocationDto row)
            { using var form = Upload(row.Version); using var response = await doctor.PostAsync($"{own}/{row.Id}/submissions", form); Check(response.StatusCode == HttpStatusCode.Created && NoStore(response), "Multipart submission returns a private pending 201"); return (await response.Content.ReadFromJsonAsync<PracticeLocationDto>())!; }
            MultipartFormDataContent Upload(string version, bool license = true)
            {
                var form = new MultipartFormDataContent(); form.Add(new StringContent(version), "Version"); form.Add(new StringContent("Approved"), "Status");
                foreach (var field in license ? new[] { "FacilityLicense", "WorkAuthorization" } : new[] { "WorkAuthorization" })
                { var file = new ByteArrayContent(DoctorReviewFixture.Evidence); file.Headers.ContentType = new("application/pdf"); form.Add(file, field, "synthetic-facility.pdf"); } return form;
            }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
