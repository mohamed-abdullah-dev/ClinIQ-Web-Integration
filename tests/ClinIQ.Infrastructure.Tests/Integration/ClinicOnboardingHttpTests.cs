using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinIQ.Application.Features.Clinics;
using ClinIQ.Application.Features.Clinics.Commands;
using ClinIQ.Domain.Clinics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class ClinicOnboardingHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpAllowsNonDoctorOnboardingAndScopedManagementWithPatientOnlyMobile()
    {
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var other = await fixture.UserAsync("ClinicApplicant"); var patient = await fixture.UserAsync("Patient");
        var otherClinic = await PublishedOtherClinic(other.Id);
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "clinic-onboarding-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts);
        var apiRoot = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(apiRoot, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "200"; start.Environment["Auth__RateLimit__PermitLimit"] = "100";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); }; Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        const string apps = "/api/clinic-applications", managed = "/api/managed-clinics", review = "/api/admin/clinic-applications";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35)); using var anonymous = Client(false); using var applicant = Client(true); using var admin = Client(true); using var stranger = Client(true); using var mobile = Client(false);
            foreach (var path in new[] { apps, managed, review }) { using var response = await anonymous.GetAsync(path); Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous clinic routes are no-store 401"); }
            var email = $"http-applicant-{Guid.NewGuid():N}@example.test";
            var input = ClinicTestData.Input() with { ApplicantCapacity = ClinicApplicantCapacity.AuthorizedRepresentative, AuthorityBasis = ManagementAuthorityBasis.SignedDelegation };
            ClinicApplicantReceipt receipt;
            using (var form = IntakeForm(email, input))
            using (var response = await anonymous.PostAsync("/api/auth/clinic-applicants/register", form))
            {
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response) && !response.Headers.Contains("Set-Cookie"), "Combined signup returns private pending 201 without login cookies");
                receipt = (await response.Content.ReadFromJsonAsync<ClinicApplicantReceipt>())!;
                Check(receipt.Status == "PendingReview" && receipt.EmailConfirmationRequired && receipt.EmailDeliveryStatus == "DevelopmentFile", "Account and full application are submitted together before confirmation");
                Check(!(await response.Content.ReadAsStringAsync()).Contains("accessToken", StringComparison.Ordinal), "Intake response contains no session credential");
            }
            applicant.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(applicant));
            using (var response = await applicant.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden && !response.Headers.Contains("Set-Cookie"), "Unconfirmed applicant cannot log in");
            var token = ConfirmationToken(email);
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/confirm-email", new { userId = receipt.UserId, token })) Check(response.StatusCode == HttpStatusCode.NoContent, "Applicant confirms email through the real API");
            using (var response = await applicant.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("Auth.ClinicPendingReview", StringComparison.Ordinal) && !response.Headers.Contains("Set-Cookie"), "Confirmed applicant remains blocked until Admin approval");
            foreach (var path in new[] { apps, managed, review }) { using var response = await applicant.GetAsync(path); Check(response.StatusCode == HttpStatusCode.Unauthorized, "Pending applicant has no tracking or management session"); }
            await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!); await HttpSessionTestSupport.WebLoginAsync(stranger, other.Email!);
            foreach (var account in new[] { email, fixture.Admin.Email! })
            { using var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email = account, password = DoctorReviewFixture.Password }); Check(response.StatusCode == HttpStatusCode.Forbidden, "Applicant and Admin mobile logins are denied"); }
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email = patient.Email, password = DoctorReviewFixture.Password }))
            { Check(response.StatusCode == HttpStatusCode.OK, "Patient mobile login remains available"); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); mobile.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString()); }
            using (var response = await mobile.GetAsync(apps)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient mobile cannot open clinic applications");
            var details = (await admin.GetFromJsonAsync<ClinicApplicationDetail>($"{review}/{receipt.ApplicationId}"))!;
            var pending = details.Application; var first = details.CurrentSubmission!; var doc = first.Documents[0];
            Check(first.Documents.Length == 3 && pending.ApplicantUserId == receipt.UserId && pending.TargetClinicId is null, "Full intake derives account and new-clinic scope despite forged fields");
            using (var response = await stranger.GetAsync($"{apps}/{pending.Id}/submissions/{first.Id}/documents/{doc.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Other managers cannot download applicant identity evidence");
            using (var response = await admin.GetAsync($"{review}/{pending.Id}/submissions/{first.Id}/documents/{doc.Id}")) Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Admin can privately review intake evidence");
            using (var response = await admin.PostAsJsonAsync($"{review}/{pending.Id}/submissions/{first.Id}/approve", new { reason = "Unchecked", version = pending.Version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Approval requires all four attestations");
            using (var response = await admin.PostAsJsonAsync($"{review}/{pending.Id}/submissions/{first.Id}/request-information", new { reason = "Correct the operator", version = pending.Version })) Check(response.StatusCode == HttpStatusCode.OK, "Admin requests information without granting login");
            using (var response = await applicant.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("Auth.ClinicNeedsInformation", StringComparison.Ordinal), "Information-requested applicant still cannot login");
            ClinicApplicantReceipt resubmitted;
            using (var form = IntakeForm(email, input with { LicensedOperator = "Corrected operator" }))
            using (var response = await anonymous.PostAsync("/api/auth/clinic-applicants/register", form))
            { Check(response.StatusCode == HttpStatusCode.Created && !response.Headers.Contains("Set-Cookie"), "Existing applicant resubmits corrected form without Login"); resubmitted = (await response.Content.ReadFromJsonAsync<ClinicApplicantReceipt>())!; }
            Check(receipt.UserId == resubmitted.UserId && receipt.ApplicationId == resubmitted.ApplicationId && receipt.SubmissionId != resubmitted.SubmissionId, "Resubmission preserves account/application and creates new immutable evidence");
            var second = (await admin.GetFromJsonAsync<ClinicApplicationDetail>($"{review}/{receipt.ApplicationId}"))!.Application;
            var approved = await Approved(second);
            await HttpSessionTestSupport.WebLoginAsync(applicant, email);
            Check(approved.Status == "Approved" && approved.ApprovedClinicId is not null && approved.ApprovedBranchId is not null, "Approval publishes the clinic and first licensed branch");
            Check((await MeRoles(applicant)).SequenceEqual(new[] { "ClinicManager" }), "First login after approval grants only scoped management, never doctor powers");
            var clinic = (await applicant.GetFromJsonAsync<ManagedClinicDto>($"{managed}/{approved.ApprovedClinicId}"))!;
            var branches = (await applicant.GetFromJsonAsync<ManagedBranchPage>($"{managed}/{clinic.Id}/branches"))!; Check(branches.TotalCount == 1 && branches.Items[0].TimeZoneId == "Africa/Cairo", "Manager sees the approved first branch in Cairo timezone");
            using (var response = await stranger.GetAsync($"{managed}/{clinic.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Another manager's global role grants no access to this clinic");
            using (var response = await stranger.PostAsJsonAsync($"{managed}/{clinic.Id}/branch-applications", ClinicTestData.Input())) Check(response.StatusCode == HttpStatusCode.NotFound, "Another manager cannot submit a branch for this clinic");
            using (var response = await applicant.GetAsync($"{managed}/{clinic.Id}/branches/{otherClinic.ApprovedBranchId}")) Check(response.StatusCode == HttpStatusCode.NotFound, "A branch ID cannot escape its clinic scope");
            var history = (await applicant.GetFromJsonAsync<ClinicSubmissionPage>($"{apps}/{approved.Id}/submissions"))!; Check(history.TotalCount == 2 && history.Items[0].Status == "NeedsInformation" && history.Items[0].ReviewReason == "Correct the operator" && history.Items[1].Details.LicensedOperator == "Corrected operator", "Old applicant evidence and reasons remain in immutable review history");
            var branchInput = ClinicTestData.Input() with { ClinicName = "Forged shared name", BranchName = "Second HTTP branch" }; ClinicApplicationDto branchDraft;
            using (var response = await applicant.PostAsJsonAsync($"{managed}/{clinic.Id}/branch-applications", branchInput))
            { Check(response.StatusCode == HttpStatusCode.Created, "Current manager can propose a further branch"); branchDraft = (await response.Content.ReadFromJsonAsync<ClinicApplicationDto>())!; Check(branchDraft.TargetClinicId == clinic.Id && branchDraft.Details.ClinicName == clinic.Name, "Server binds the new branch request to the managed clinic's real shared name"); }
            Check((await applicant.GetFromJsonAsync<ManagedBranchPage>($"{managed}/{clinic.Id}/branches"))!.TotalCount == 1, "Draft branch is not automatically activated");
            await Approved(await Submitted(branchDraft, false)); Check((await applicant.GetFromJsonAsync<ManagedBranchPage>($"{managed}/{clinic.Id}/branches"))!.TotalCount == 2, "Further branch becomes available only after its separate review");
            using (var form = IntakeForm(patient.Email!, ClinicTestData.Input()))
            using (var response = await anonymous.PostAsync("/api/auth/clinic-applicants/register", form)) Check(response.StatusCode == HttpStatusCode.Created && (await response.Content.ReadFromJsonAsync<ClinicApplicantReceipt>())!.UserId == patient.Id, "Existing patient uses full intake with their own verified credentials");
            using var patientWeb = Client(true); patientWeb.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(patientWeb));
            using (var response = await patientWeb.PostAsJsonAsync("/api/auth/web/login", new { email = patient.Email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient clinic application does not unlock Web before approval");
            Check((await MeRoles(mobile)).SequenceEqual(new[] { "Patient" }), "Patient mobile session stays Patient only");
            using (var response = await anonymous.GetAsync("/openapi/v1.json")) Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("/api/auth/clinic-applicants/register", StringComparison.Ordinal), "OpenAPI includes clinic account and review contracts");
            await using (var db = fixture.Database.CreateContext())
            { Check(!await db.DoctorProfiles.AnyAsync(x => x.UserId == receipt.UserId), "Approved non-doctor manager has no fabricated doctor profile"); Check(await db.ClinicManagementMemberships.CountAsync(x => x.ClinicId == clinic.Id) == 1, "Branch expansion preserves the original single manager membership"); }
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks })); output.WriteLine($"CLINIC_ONBOARDING_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<string[]> MeRoles(HttpClient c) { using var response = await c.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray(); }
            async Task<ClinicApplicationDto> Submitted(ClinicApplicationDto row, bool delegation) { using var form = Form(row.Version, delegation); using var response = await applicant.PostAsync($"{apps}/{row.Id}/submissions", form); Check(response.StatusCode == HttpStatusCode.Created && NoStore(response), "Multipart evidence creates a private pending submission"); return (await response.Content.ReadFromJsonAsync<ClinicApplicationDto>())!; }
            async Task<ClinicApplicationDto> Approved(ClinicApplicationDto row) { using var response = await admin.PostAsJsonAsync($"{review}/{row.Id}/submissions/{row.CurrentSubmissionId}/approve", new { reason = "License, identity and authority verified independently", version = row.Version, licenseDetailsVerified = true, identityVerified = true, authorityVerified = true, independentlyCorroborated = true }); Check(response.StatusCode == HttpStatusCode.OK, "Admin approves only the current verified submission"); return (await response.Content.ReadFromJsonAsync<ClinicApplicationDto>())!; }
            string ConfirmationToken(string recipient) { foreach (var file in Directory.GetFiles(Path.Combine(fixture.PrivateRoot, "development-emails"), "*.json")) { using var json = JsonDocument.Parse(File.ReadAllText(file)); if (json.RootElement.GetProperty("To").GetString() == recipient) return QueryHelpers.ParseQuery(new Uri(json.RootElement.GetProperty("ConfirmationUrl").GetString()!).Query)["token"].ToString(); } throw new InvalidOperationException("Expected private development confirmation email."); }
            MultipartFormDataContent Form(string version, bool delegation) { var form = new MultipartFormDataContent(); form.Add(new StringContent(version), "Version"); foreach (var name in delegation ? new[] { "FacilityLicense", "ApplicantIdentity", "ManagementDelegation" } : new[] { "FacilityLicense", "ApplicantIdentity" }) { var file = new ByteArrayContent(DoctorReviewFixture.Evidence); file.Headers.ContentType = new("application/pdf"); form.Add(file, name, "synthetic.pdf"); } return form; }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
    private static MultipartFormDataContent IntakeForm(string email, ClinicApplicationData input)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in new[] { ("FullName", "Synthetic non-doctor applicant"), ("Email", email), ("Password", DoctorReviewFixture.Password),
            ("ClinicName", input.ClinicName), ("Description", input.Description), ("BranchName", input.BranchName), ("Address", input.Address),
            ("PublicPhone", input.PublicPhone), ("LicensingAuthority", input.LicensingAuthority), ("LicenseNumber", input.LicenseNumber), ("LicensedOperator", input.LicensedOperator),
            ("ApplicantCapacity", ((int)input.ApplicantCapacity).ToString()), ("AuthorityBasis", ((int)input.AuthorityBasis).ToString()), ("Role", "Admin"), ("EmailConfirmed", "true"), ("ApplicantUserId", Guid.NewGuid().ToString()) })
            form.Add(new StringContent(value), name);
        foreach (var field in input.AuthorityBasis == ManagementAuthorityBasis.SignedDelegation ? new[] { "FacilityLicense", "ApplicantIdentity", "ManagementDelegation" } : new[] { "FacilityLicense", "ApplicantIdentity" })
        { var file = new ByteArrayContent(DoctorReviewFixture.Evidence); file.Headers.ContentType = new("application/pdf"); form.Add(file, field, "synthetic.pdf"); }
        return form;
    }
    private async Task<ClinicApplicationDto> PublishedOtherClinic(Guid user)
    {
        var draft = (await fixture.AsAsync(user, p => p.GetRequiredService<IClinicService>().CreateAsync(ClinicTestData.Input(), null, default))).Value; var docs = ClinicTestData.Uploads(); ClinicApplicationDto pending;
        try { pending = (await fixture.AsAsync(user, p => p.GetRequiredService<IClinicService>().SubmitAsync(draft.Id, draft.Version, docs, default))).Value; } finally { foreach (var doc in docs) await doc.Content.DisposeAsync(); }
        return (await fixture.AsAsync(fixture.Admin.Id, p => p.GetRequiredService<IClinicService>().ReviewAsync(pending.Id, pending.CurrentSubmissionId!.Value, ClinicApplicationStatus.Approved, "Synthetic setup approval", pending.Version, true, true, true, true, default))).Value;
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
