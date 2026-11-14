using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.PracticeLinks;
using ClinIQ.Domain.PracticeLinks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLinkHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealWebConsentWorkflowEnforcesCsrfRolesExplicitDecisionAndPublishesOpenApi()
    {
        var place = await new SchedulingTests(fixture).Place(); var branch = await new PremisesCorrectionTests(fixture).Branch();
        await using var db = fixture.Database.CreateContext();
        var doctorEmail = (await db.Users.SingleAsync(u => u.Id == place.Doctor)).Email!;
        var managerEmail = (await db.Users.SingleAsync(u => u.Id == branch.Owner)).Email!;
        var artifact = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p11-link-http", Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(artifact);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "200"; start.Environment["FeedbackNotifications__WorkerEnabled"] = "false";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool ok, string label) { Assert.True(ok, label); checks.Add(label); }
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(45));
            using var guest = Client(); using var doctor = Client(); using var manager = Client(); using var admin = Client();
            await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail); await HttpSessionTestSupport.WebLoginAsync(manager, managerEmail);
            await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            const string path = "/api/practice-links";
            using (var response = await guest.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Guest rejected");
            using (var response = await admin.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Admin has no implicit linking role");
            var source = (await doctor.GetFromJsonAsync<LinkSourceDto>($"{path}/preview?locationId={place.Place.Id}&branchId={branch.Branch.Id}"))!;
            Check(source.Terms == PracticeLinkRequest.CurrentTerms && source.ConsultationFee == place.Place.ConsultationFee, "Preview supplies agreed fee and policy");
            var input = new LinkCreateInput(source.LocationId, source.BranchId, source.LocationVersion, source.BranchVersion, true, true);
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await doctor.PostAsJsonAsync(path, input)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Create requires CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(doctor));
            using (var response = await doctor.PostAsJsonAsync(path, input with { SamePremises = false })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Same premises consent required");
            LinkRequestDto request;
            using (var response = await doctor.PostAsJsonAsync(path, input))
            { Check(response.StatusCode == HttpStatusCode.Created && response.Headers.Location is not null && response.Headers.CacheControl?.NoStore == true, "Doctor submits private pending request"); request = (await response.Content.ReadFromJsonAsync<LinkRequestDto>())!; }
            Check(request.Status == PracticeLinkStatus.Pending && (await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == place.Place.Id)).ClinicBranchId is null, "Doctor consent alone does not link");
            Check((await manager.GetFromJsonAsync<LinkPage>(path))!.Items.Any(r => r.Id == request.Id), "Scoped manager sees request");
            using (var response = await manager.GetAsync(path + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Bounded pagination");
            var decision = new LinkDecisionInput(true, request.Version, "Same premises and managed policy accepted", true, true);
            using (var response = await doctor.PostAsJsonAsync($"{path}/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.NotFound, "Doctor cannot supply manager consent");
            using (var response = await manager.PostAsJsonAsync($"{path}/{request.Id}/decision", new { version = request.Version, reason = "Missing choice" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Missing outcome is not implicit rejection");
            using (var response = await manager.PostAsJsonAsync($"{path}/{request.Id}/decision", decision with { AcceptTerms = false })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Manager must accept policy");
            manager.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await manager.PostAsJsonAsync($"{path}/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Manager decision requires CSRF");
            manager.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(manager));
            using (var response = await manager.PostAsJsonAsync($"{path}/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.OK, "Manager accepts");
            using (var response = await manager.PostAsJsonAsync($"{path}/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.OK, "Acceptance retry idempotent");
            var accepted = (await doctor.GetFromJsonAsync<LinkRequestDto>($"{path}/{request.Id}"))!;
            Check(accepted.Status == PracticeLinkStatus.Accepted && accepted.DecidedByUserId == branch.Owner, "Consent history retains actor");
            var linked = await db.DoctorPracticeLocations.AsNoTracking().SingleAsync(p => p.Id == place.Place.Id);
            Check(linked.ClinicBranchId == branch.Branch.Id && linked.SourceLinkId == request.Id && linked.CurrentSubmissionId == place.Place.CurrentSubmissionId, "Original location and evidence retained");
            using (var response = await doctor.PostAsJsonAsync($"{path}/{request.Id}/withdraw", new LinkWithdrawInput(accepted.Version, "Cannot undo accepted link"))) Check(response.StatusCode == HttpStatusCode.Conflict, "No silent unlink");
            using (var response = await guest.GetAsync("/openapi/v1.json")) Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("/api/practice-links/{id}/decision", StringComparison.Ordinal), "OpenAPI renders complete linking contract");
            await File.WriteAllTextAsync(Path.Combine(artifact, "report.json"), JsonSerializer.Serialize(new { Count = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"P11_LINK_HTTP_CHECKS:{checks.Count}; artifact:{artifact}");
            HttpClient Client() => new(new HttpClientHandler { UseCookies = true, CookieContainer = new() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(artifact, "api.log"), log.ToString());
        }
    }
}
