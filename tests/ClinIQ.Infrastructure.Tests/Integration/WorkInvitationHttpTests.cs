using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Invitations;
using ClinIQ.Application.Features.Invitations.Commands;
using ClinIQ.Application.Features.PracticeLocations;
using ClinIQ.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class WorkInvitationHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpInvitationsReceptionistOnboardingScopeAndManagedFeeWorkEndToEnd()
    {
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var setup = new PremisesCorrectionTests(fixture); var helper = new WorkInvitationTests(fixture); var (managerId, _, branch) = await setup.Branch();
        var (doctorId, independent) = await setup.Place(); var other = await PracticeLocationTestData.ApprovedDoctor(fixture); var patient = await fixture.UserAsync("Patient");
        await using var db = fixture.Database.CreateContext(); var managerEmail = await db.Users.Where(x => x.Id == managerId).Select(x => x.Email).SingleAsync(); var doctorEmail = await db.Users.Where(x => x.Id == doctorId).Select(x => x.Email).SingleAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p05-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot; start.Environment["Auth__Sessions__PermitLimit"] = "200";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start }; process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); }; Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool yes, string label) { Assert.True(yes, label); checks.Add(label); }
        const string invites = "/api/work-invitations", work = "/api/workforce";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35)); using var anonymous = Client(false); using var manager = Client(true); using var doctor = Client(true); using var stranger = Client(true); using var receptionist = Client(true); using var mobile = Client(false);
            await HttpSessionTestSupport.WebLoginAsync(manager, managerEmail!); await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail!); await HttpSessionTestSupport.WebLoginAsync(stranger, other.Email);
            using (var response = await anonymous.GetAsync(invites)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Invitations are private no-store 401 without authentication");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email = patient.Email, password = DoctorReviewFixture.Password }))
            { Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); mobile.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString()); }
            using (var response = await mobile.GetAsync(invites)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient mobile cannot access workforce invitations");
            manager.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await manager.PostAsJsonAsync(invites, new IssueInvitationCommand(WorkInvitationKind.Doctor, branch.Id, null, doctorEmail!, "Agreed HTTP terms", 80, "EGP"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Issuing a Web invitation requires CSRF");
            manager.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(manager));
            using (var response = await stranger.PostAsJsonAsync(invites, new IssueInvitationCommand(WorkInvitationKind.Doctor, branch.Id, null, doctorEmail!, "Wrong scope", 80, "EGP"))) Check(response.StatusCode == HttpStatusCode.NotFound, "Doctor role does not authorize another clinic branch");
            var first = await Issue(manager, new(WorkInvitationKind.Doctor, branch.Id, null, doctorEmail!, "Agreed HTTP terms", 80, "EGP"));
            using (var response = await stranger.GetAsync($"{invites}/{first.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "A different verified recipient cannot view the invitation");
            using (var response = await stranger.PostAsJsonAsync($"{invites}/{first.Id}/accept", Response(first))) Check(response.StatusCode == HttpStatusCode.NotFound, "Even the correct invitation secret cannot authorize a wrong recipient");
            var inbox = (await doctor.GetFromJsonAsync<InvitationPage>(invites))!; Check(inbox.Items.Any(x => x.Id == first.Id), "Recipient sees their own invitation and agreed fee");
            using (var response = await doctor.PostAsJsonAsync($"{invites}/{first.Id}/accept", Response(first))) Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Approved doctor accepts branch terms and EGP fee");
            var places = (await doctor.GetFromJsonAsync<PracticeLocationPage>("/api/doctors/me/practice-locations"))!; var managed = Assert.Single(places.Items, x => x.ClinicBranchId == branch.Id);
            Check(managed.ConsultationFee == 80 && places.Items.Any(x => x.Id == independent.Id && x.ClinicBranchId == null), "Managed affiliation and pre-existing standalone record remain distinct");
            using (var response = await doctor.PutAsJsonAsync($"/api/doctors/me/practice-locations/{managed.Id}/fee", new { consultationFee = 90, currency = "EGP", version = managed.Version })) Check(response.StatusCode == HttpStatusCode.Conflict, "Doctor cannot bypass manager price authority through independent fee route");
            using (var response = await manager.PutAsJsonAsync($"{work}/locations/{managed.Id}/fee", new { currency = "EGP", version = managed.Version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Managed fee input must explicitly include amount, including zero");
            using (var response = await manager.PutAsJsonAsync($"{work}/locations/{managed.Id}/fee", new { amount = 0, currency = "EGP", version = managed.Version }))
            { Check(response.StatusCode == HttpStatusCode.OK, "Manager changes joined doctor's price without another acceptance"); var notice = (await response.Content.ReadFromJsonAsync<FeeChangeDto>())!; Check(notice.DeliveryStatus == "DevelopmentFile" && notice.PreviousFee == 80 && notice.NewFee == 0, "Price write has a durable, truthfully delivered development notice"); }
            var email = $"http-reception-{Guid.NewGuid():N}@example.test"; var receptionInvite = await Issue(manager, new(WorkInvitationKind.Receptionist, branch.Id, null, email, "Branch reception terms")); ReceptionistRegistrationReceipt receipt;
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/receptionists/register", new { invitationId = receptionInvite.Id, secret = helper.Secret(receptionInvite.Id), fullName = "HTTP receptionist", email, password = DoctorReviewFixture.Password, role = "Admin", emailConfirmed = true }))
            { Check(response.StatusCode == HttpStatusCode.Created && !response.Headers.Contains("Set-Cookie"), "Invitation-only registration creates no login cookies or forged role"); receipt = (await response.Content.ReadFromJsonAsync<ReceptionistRegistrationReceipt>())!; Check(receipt.EmailConfirmationRequired, "Receptionist email confirmation is still mandatory"); }
            receptionist.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(receptionist));
            using (var response = await receptionist.PostAsJsonAsync("/api/auth/web/login", new { email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Unconfirmed receptionist cannot log in");
            var token = ConfirmationToken(email);
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/confirm-email", new { userId = receipt.UserId, token })) Check(response.StatusCode == HttpStatusCode.NoContent, "Receptionist confirms email through real HTTP");
            await HttpSessionTestSupport.WebLoginAsync(receptionist, email);
            Check((await receptionist.GetFromJsonAsync<AuthorizedDoctorDto[]>($"{work}/reception/doctors"))!.Length == 0, "Pending invitation grants no doctor or patient scope");
            using (var response = await anonymous.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Receptionist remains excluded from Mobile login");
            using (var response = await receptionist.PostAsJsonAsync($"{invites}/{receptionInvite.Id}/accept", Response(receptionInvite))) Check(response.StatusCode == HttpStatusCode.OK, "Receptionist explicitly accepts assignment");
            var doctors = (await receptionist.GetFromJsonAsync<AuthorizedDoctorDto[]>($"{work}/reception/doctors"))!; var selected = Assert.Single(doctors);
            var scopes = (await receptionist.GetFromJsonAsync<AuthorizedPlaceDto[]>($"{work}/reception/doctors/{selected.DoctorProfileId}/locations"))!;
            Check(scopes.Length == 1 && scopes[0].LocationId == managed.Id, "Doctor-first selection exposes branch scope and excludes their standalone location");
            using (var response = await receptionist.GetAsync($"{work}/reception/doctors/{(await db.DoctorProfiles.Where(x => x.UserId == other.UserId).Select(x => x.Id).SingleAsync())}/locations")) Check(response.StatusCode == HttpStatusCode.NotFound, "Receptionist cannot enumerate another doctor's places");
            using (var response = await receptionist.PostAsJsonAsync(invites, new IssueInvitationCommand(WorkInvitationKind.Receptionist, branch.Id, null, "forged@example.test", "Unauthorized"))) Check(response.StatusCode == HttpStatusCode.NotFound, "Receptionist cannot delegate new permissions");
            var assignment = Assert.Single((await manager.GetFromJsonAsync<AssignmentDto[]>($"{work}/receptionist-assignments"))!);
            using (var response = await manager.PostAsJsonAsync($"{work}/receptionist-assignments/{assignment.Id}/revoke", new { version = assignment.Version, reason = "Employment ended" })) Check(response.StatusCode == HttpStatusCode.OK, "Current manager revokes with reason and history");
            using (var response = await receptionist.GetAsync($"{work}/reception/doctors")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Revocation invalidates last-scope access in an existing session without logout");
            using (var response = await anonymous.GetAsync("/openapi/v1.json")) Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("/api/work-invitations/{id}/accept", StringComparison.Ordinal), "OpenAPI publishes invitation and workforce contracts");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks })); output.WriteLine($"P05_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<InvitationDto> Issue(HttpClient client, IssueInvitationCommand command)
            { using var response = await client.PostAsJsonAsync(invites, command); Check(response.StatusCode == HttpStatusCode.Created && NoStore(response) && response.Headers.Location is not null, "Invitation issue returns private 201 with Location"); var body = await response.Content.ReadAsStringAsync(); Check(!body.Contains("secretHash", StringComparison.OrdinalIgnoreCase) && !body.Contains("InvitationUrl", StringComparison.OrdinalIgnoreCase), "Issued invitation response contains no secret or private delivery URL"); return JsonSerializer.Deserialize<InvitationDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
            object Response(InvitationDto row) => new { secret = helper.Secret(row.Id), version = row.Version };
            string ConfirmationToken(string recipient)
            { foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.PrivateRoot, "development-emails"), "*.json")) { using var json = JsonDocument.Parse(File.ReadAllText(path)); if (json.RootElement.GetProperty("To").GetString() == recipient) { var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(json.RootElement.GetProperty("ConfirmationUrl").GetString()!).Query); return query["token"].ToString(); } } throw new InvalidOperationException("Synthetic confirmation email missing."); }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
    private static bool NoStore(HttpResponseMessage r) => r.Headers.CacheControl?.NoStore == true;
}
