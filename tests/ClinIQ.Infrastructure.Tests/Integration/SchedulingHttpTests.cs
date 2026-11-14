using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Scheduling;
using ClinIQ.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SchedulingHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpSchedulingEnforcesDoctorOwnershipPublicationAndOperationalControls()
    {
        var setup = new SchedulingTests(fixture); var (doctorId, place) = await setup.Place(); var otherPlace = await setup.SecondPlace(doctorId);
        var otherDoctor = await PracticeLocationTestData.ApprovedDoctor(fixture); var (managerId, _, _) = await new PremisesCorrectionTests(fixture).Branch();
        var patient = await fixture.UserAsync("Patient"); var reception = await fixture.UserAsync("Receptionist");
        var invitationHelper = new WorkInvitationTests(fixture); var invitation = (await invitationHelper.Issue(doctorId, WorkInvitationKind.Receptionist, null, place.Id, reception.Email!)).Value;
        Assert.True((await invitationHelper.Respond(reception.Id, invitation)).IsSuccess);
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow; var date = setup.Tomorrow;
        await using var db = fixture.Database.CreateContext(); var doctorEmail = await db.Users.Where(x => x.Id == doctorId).Select(x => x.Email).SingleAsync(); var managerEmail = await db.Users.Where(x => x.Id == managerId).Select(x => x.Email).SingleAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p06-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts); var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development"; start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot; start.Environment["Auth__Sessions__PermitLimit"] = "200";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously); using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); }; process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine(); var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        const string path = "/api/doctors/me/scheduling";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35)); using var anonymous = Client(false); using var doctor = Client(true); using var stranger = Client(true); using var manager = Client(true); using var staff = Client(true); using var mobile = Client(false);
            await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail!); await HttpSessionTestSupport.WebLoginAsync(stranger, otherDoctor.Email); await HttpSessionTestSupport.WebLoginAsync(manager, managerEmail!); await HttpSessionTestSupport.WebLoginAsync(staff, reception.Email!);
            using (var response = await anonymous.GetAsync(path + "/rules")) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Scheduling is private no-store 401 without login");
            foreach (var client in new[] { manager, staff }) { using var response = await client.GetAsync(path + "/rules"); Check(response.StatusCode == HttpStatusCode.Forbidden, "Manager and Receptionist do not inherit doctor schedule powers"); }
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email = patient.Email, password = DoctorReviewFixture.Password })) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); mobile.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString()); }
            using (var response = await mobile.GetAsync(path + "/rules")) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient mobile cannot manage doctor schedules");
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/rules", Rule())) Check(response.StatusCode == HttpStatusCode.BadRequest, "Rule creation requires Web CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(doctor));
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/rules", new { day = (int)date.DayOfWeek, start = "18:00:00", end = "20:00:00", maxCapacity = 10, effectiveFrom = date })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Booking cutoff must be explicitly provided even when zero");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/rules", Rule("22:00:00", "18:00:00"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Overnight rules are rejected");
            ScheduleRuleDto rule;
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/rules", Rule())) { Check(response.StatusCode == HttpStatusCode.Created && NoStore(response) && response.Headers.Location is not null, "Rule returns private 201 and a detail Location"); rule = (await response.Content.ReadFromJsonAsync<ScheduleRuleDto>())!; }
            Check((await db.ScheduleRules.SingleAsync(x => x.Id == rule.Id)).UserId == doctorId, "Rule ownership is resolved from the authenticated doctor despite forged fields");
            using (var response = await stranger.GetAsync($"{path}/rules/{rule.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Foreign doctor cannot read rule");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/preview", Window())) { Check(response.StatusCode == HttpStatusCode.OK, "Publication preview succeeds"); var preview = (await response.Content.ReadFromJsonAsync<PublicationResult>())!; Check(preview.IsPreview && preview.Created == 0 && preview.Planned == 2 && preview.Items.All(x => x.SessionId == null), "Preview calculates UTC and cutoffs without creating sessions"); }
            Check(!await db.BookingSessions.AnyAsync(x => x.UserId == doctorId), "Preview leaves persistence unchanged");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/publish", Window())) { Check(response.StatusCode == HttpStatusCode.OK, "Publishing materializes dated sessions"); var publication = (await response.Content.ReadFromJsonAsync<PublicationResult>())!; Check(publication.Created == 2 && !publication.IsPreview, "Exactly two matching weekly dates were published"); }
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/publish", Window())) { var again = (await response.Content.ReadFromJsonAsync<PublicationResult>())!; Check(response.StatusCode == HttpStatusCode.OK && again.Created == 0 && again.Preserved == 2, "Repeated publication does not duplicate sessions"); }
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{place.Id}/publish", new { from = date, until = setup.Cairo.Today.AddDays(28) })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Publication beyond the 28-day horizon is rejected");
            var sessions = (await doctor.GetFromJsonAsync<SchedulePage<SessionDto>>($"{path}/sessions?from={date:yyyy-MM-dd}&until={date.AddDays(7):yyyy-MM-dd}"))!; var row = sessions.Items.Single(x => x.LocalDate == date);
            Check(row.SourceRuleId == rule.Id && row.SourceRuleVersion == rule.Version && row.BookingClosesAtUtc == row.EndsAtUtc.AddMinutes(-30), "Published session preserves provenance and calculated cutoff");
            using (var response = await stranger.PostAsJsonAsync($"{path}/sessions/{row.Id}/pause", new { version = row.Version })) Check(response.StatusCode == HttpStatusCode.NotFound, "Foreign doctor cannot pause another session");
            using (var response = await staff.PostAsJsonAsync($"{path}/sessions/{row.Id}/pause", new { version = row.Version })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Active receptionist cannot pause or edit capacity");
            var original = row; row = await Edit("cutoff", new { value = 180, version = row.Version }); Check(row.ClosesAtOrBeforeStart && row.BookingClosesAtUtc < row.StartsAtUtc, "Long cutoff effect is returned explicitly without silent clamping");
            row = await Action("pause", new { version = row.Version }); row = await Edit("cutoff", new { value = 0, version = row.Version }); Check(row.BookingPaused && !row.AcceptingNewBookings, "Cutoff edits preserve a manual pause");
            row = await Action("resume", new { version = row.Version }); Check(!row.BookingPaused && row.AcceptingNewBookings, "Resume rechecks cutoff, capacity and live resources");
            using (var response = await doctor.PutAsJsonAsync($"{path}/sessions/{row.Id}/capacity", new { value = 0, version = row.Version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Nonpositive capacity is rejected");
            using (var response = await doctor.PutAsJsonAsync($"{path}/sessions/{row.Id}/capacity", new { value = 5, version = original.Version })) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale session version cannot overwrite controls");
            row = await Edit("capacity", new { value = 5, version = row.Version }); Check(row.MaxCapacity == 5, "Responsible doctor can change capacity");
            row = await Edit("times", new { start = "19:00:00", end = "21:00:00", version = row.Version }); Check(row.StartsLocal == new TimeOnly(19, 0), "Unstarted unallocated session times can be explicitly changed");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{otherPlace.Id}/extra-sessions", new { date, start = "20:00:00", end = "22:00:00", maxCapacity = 10, bookingCutoffMinutes = 0 })) Check(response.StatusCode == HttpStatusCode.Conflict, "Extra session cannot overlap the doctor at another location");
            using (var response = await doctor.PostAsJsonAsync($"{path}/locations/{otherPlace.Id}/extra-sessions", new { date, start = "21:00:00", end = "22:00:00", maxCapacity = 10, bookingCutoffMinutes = 0 })) Check(response.StatusCode == HttpStatusCode.Created, "Adjacent extra session at another location is permitted");
            ScheduleExceptionDto holiday;
            using (var response = await doctor.PostAsJsonAsync(path + "/exceptions", new { date, locationId = place.Id, reason = "Location holiday" })) { Check(response.StatusCode == HttpStatusCode.Created, "Scoped holiday creates a private exception"); holiday = (await response.Content.ReadFromJsonAsync<ScheduleExceptionDto>())!; }
            row = (await doctor.GetFromJsonAsync<SessionDto>($"{path}/sessions/{row.Id}"))!; Check(row.Status == "Cancelled" && row.CancellationReason == "Location holiday", "Holiday preserves the affected occurrence as cancelled history");
            using (var response = await doctor.PostAsJsonAsync($"{path}/exceptions/{holiday.Id}/remove", new { version = holiday.Version, reason = "Holiday withdrawn" })) Check(response.StatusCode == HttpStatusCode.OK, "Exception can be removed with version and reason");
            using (var response = await doctor.PostAsJsonAsync($"{path}/sessions/{row.Id}/resume", new { version = row.Version })) Check(response.StatusCode == HttpStatusCode.Conflict, "Removing holiday never resurrects a cancelled occurrence");
            using (var response = await doctor.GetAsync($"{path}/sessions?from={date:yyyy-MM-dd}&until={date:yyyy-MM-dd}&pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Session list pagination is bounded");
            using (var response = await anonymous.GetAsync("/openapi/v1.json")) Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("/api/doctors/me/scheduling/locations/{locationId}/preview", StringComparison.Ordinal), "OpenAPI includes scheduling and preview contracts");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks })); output.WriteLine($"P06_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            object Rule(string start = "18:00:00", string end = "20:00:00") => new { day = (int)date.DayOfWeek, start, end, maxCapacity = 10, bookingCutoffMinutes = 30, effectiveFrom = date, userId = fixture.Admin.Id, doctorProfileId = Guid.NewGuid() };
            object Window() => new { from = date, until = date.AddDays(7) };
            async Task<SessionDto> Edit(string operation, object body) { using var response = await doctor.PutAsJsonAsync($"{path}/sessions/{row.Id}/{operation}", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<SessionDto>())!; }
            async Task<SessionDto> Action(string operation, object body) { using var response = await doctor.PostAsJsonAsync($"{path}/sessions/{row.Id}/{operation}", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<SessionDto>())!; }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
