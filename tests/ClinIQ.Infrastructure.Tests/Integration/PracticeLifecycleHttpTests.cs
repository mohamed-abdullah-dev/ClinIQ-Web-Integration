using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.PracticeLifecycle;
using ClinIQ.Domain.PracticeLifecycle;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PracticeLifecycleHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealWebDepartureAdminPreviewCancellationAndFinalizationProtectRetainedPatientBooking()
    {
        var schedule = new SchedulingTests(fixture); var (doctorId, place) = await schedule.Place();
        var otherPlace = await schedule.SecondPlace(doctorId); fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var session = (await schedule.Extra(doctorId, place.Id, schedule.Tomorrow, 18, 20)).Value;
        var helper = new AppointmentTests(fixture); var patient = await helper.Patient(); var booking = (await helper.Book(patient, session.Id)).Value.Appointment;
        session = (await schedule.Call(doctorId, x => x.GetSessionAsync(session.Id, default))).Value;
        await using var db = fixture.Database.CreateContext();
        var doctorEmail = (await db.Users.SingleAsync(u => u.Id == doctorId)).Email!;
        var patientEmail = (await db.Users.SingleAsync(u => u.Id == patient)).Email!;
        var artifact = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p11-lifecycle-http", Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(artifact);
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
        var path = $"/api/operations/locations/{place.Id}"; const string adminPath = "/api/admin/operational-emergencies";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var guest = Client(false); using var mobile = Client(false); using var doctor = Client(true); using var admin = Client(true);
            await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail); await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            using (var login = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email = patientEmail, password = DoctorReviewFixture.Password }))
            { Assert.Equal(HttpStatusCode.OK, login.StatusCode); using var j = JsonDocument.Parse(await login.Content.ReadAsStringAsync()); mobile.DefaultRequestHeaders.Authorization = new("Bearer", j.RootElement.GetProperty("accessToken").GetString()); }
            using (var response = await guest.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Scope operations require authentication");
            using (var response = await mobile.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient Mobile cannot manage operational scope");
            var scope = (await doctor.GetFromJsonAsync<LifecycleScopeDto>(path))!;
            Check(scope.State == "Active" && scope.ConfirmedCount == 1 && scope.AcceptingNewBookings, "Owner sees its concrete scope and outstanding visit");
            using (var response = await doctor.PostAsJsonAsync(path + "/finalize", new LifecycleChangeInput(scope.Version, "Too early"))) Check(response.StatusCode == HttpStatusCode.Conflict, "Finalization requires pending departure first");
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await doctor.PostAsJsonAsync(path + "/departure", new LifecycleChangeInput(scope.Version, "Leaving this location"))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Departure requires Web CSRF");
            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(doctor));
            using (var response = await doctor.PostAsJsonAsync(path + "/departure", new LifecycleChangeInput(scope.Version, "Leaving this location")))
            { Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Departure begins privately with current version"); scope = (await response.Content.ReadFromJsonAsync<LifecycleScopeDto>())!; }
            Check(scope.State == "PendingDeparture" && scope.IsActive && !scope.AcceptingNewBookings, "Pending stops new bookings while retaining operational access");
            using (var response = await doctor.PostAsJsonAsync(path + "/finalize", new LifecycleChangeInput(scope.Version, "Still has visit"))) Check(response.StatusCode == HttpStatusCode.Conflict, "Future Confirmed visit blocks finalization");
            using (var response = await guest.GetAsync($"/api/public/sessions/{session.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Pending session is no longer publicly bookable");
            using (var response = await mobile.GetAsync($"/api/patients/me/appointments/{booking.Id}")) Check(response.StatusCode == HttpStatusCode.OK, "Patient retains access to existing booking");
            using (var response = await doctor.PostAsJsonAsync($"/api/doctors/me/scheduling/sessions/{session.Id}/resume", new { version = session.Version })) Check(response.StatusCode == HttpStatusCode.Conflict && !(await response.Content.ReadAsStringAsync()).Contains("Stale", StringComparison.OrdinalIgnoreCase), "Ordinary resume cannot reopen pending scope");
            using (var response = await doctor.PostAsJsonAsync($"/api/doctors/me/scheduling/sessions/{session.Id}/cancel", new { version = session.Version, reason = "Attempted ordinary shortcut" })) Check(response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadAsStringAsync()).Contains("Lifecycle.AdminReviewRequired", StringComparison.Ordinal), "Routine session cancellation cannot bypass emergency review");
            EmergencySummary request;
            using (var response = await doctor.PostAsJsonAsync(path + "/emergencies", new EmergencyRequestInput(scope.Version, EmergencyReasonCategory.UnusablePremises, "Synthetic premises unavailable")))
            { Check(response.StatusCode == HttpStatusCode.Created && response.Headers.Location is not null, "Owner submits a concrete reasoned emergency request"); request = (await response.Content.ReadFromJsonAsync<EmergencySummary>())!; }
            Check((await mobile.GetFromJsonAsync<AppointmentDto>($"/api/patients/me/appointments/{booking.Id}"))!.Status == "Confirmed", "Submitting emergency request does not cancel the visit");
            using (var response = await doctor.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Doctor cannot inspect Admin queue");
            using (var response = await mobile.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot inspect Admin queue");
            var queue = (await admin.GetFromJsonAsync<LifecyclePage<EmergencySummary>>(adminPath))!; Check(queue.Items.Any(r => r.Id == request.Id), "Admin sees pending scoped request");
            var detail = (await admin.GetFromJsonAsync<EmergencyDetail>(adminPath + $"/{request.Id}"))!;
            Check(detail.AffectedVisits.TotalCount == 1 && detail.AffectedVisits.Items[0].Id == booking.Id && detail.PreviewHash.Length == 64, "Admin previews exactly the affected booking and concurrency hash");
            var preview = JsonSerializer.Serialize(detail.AffectedVisits); Check(!preview.Contains("patient", StringComparison.OrdinalIgnoreCase) && !preview.Contains("contact", StringComparison.OrdinalIgnoreCase), "Preview omits patient identity and contact fields");
            using (var response = await admin.GetAsync(adminPath + $"/{request.Id}?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Affected preview pages are bounded");
            var decision = new EmergencyDecisionInput(true, request.Version, "Verified unusable premises", detail.Scope.Version, detail.PreviewHash);
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Admin decision requires Web CSRF");
            admin.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(admin));
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{request.Id}/decision", new { version = request.Version, reason = "An explicit decision is required" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Omitting approve cannot silently reject a request");
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{request.Id}/decision", decision with { PreviewHash = new string('0', 64) })) Check(response.StatusCode == HttpStatusCode.Conflict, "Stale affected preview cannot be approved");
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{request.Id}/decision", decision))
            { Check(response.StatusCode == HttpStatusCode.OK, "Admin approves reviewed exception"); var approved = (await response.Content.ReadFromJsonAsync<EmergencySummary>())!; Check(approved.CancelledCount == 1 && approved.ReviewedPreviewHash == detail.PreviewHash, "Final decision retains reviewed preview and cancellation count"); }
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{request.Id}/decision", decision)) Check(response.StatusCode == HttpStatusCode.OK, "Decision retry is idempotent");
            Check(!(await admin.GetFromJsonAsync<LifecyclePage<EmergencySummary>>(adminPath))!.Items.Any(r => r.Id == request.Id), "Default Admin queue remains Pending after approval");
            var cancelled = (await mobile.GetFromJsonAsync<AppointmentDto>($"/api/patients/me/appointments/{booking.Id}"))!;
            Check(cancelled.Status == "Cancelled" && cancelled.CancellationCategory == "AdminEmergencyClosure", "Patient sees committed cancellation outcome");
            var evidence = (await admin.GetFromJsonAsync<LifecyclePage<EmergencyCancellationDto>>(adminPath + $"/{request.Id}/cancellations"))!;
            Check(evidence.Items.Count == 1 && evidence.Items[0].AppointmentId == booking.Id && evidence.Items[0].SourceVersion.Length > 0, "Admin retains immutable per-booking before-after evidence");
            Check(await db.BookingNotices.CountAsync(n => n.AppointmentId == booking.Id && n.Kind == ClinIQ.Domain.Appointments.BookingNoticeKind.Cancelled) == 2, "One durable cancellation notice exists for patient and doctor");
            scope = (await doctor.GetFromJsonAsync<LifecycleScopeDto>(path))!; Check(scope.CanFinalize && scope.ConfirmedCount == 0 && scope.CheckedInCount == 0, "Resolved commitments now permit finalization");
            using (var response = await doctor.PostAsJsonAsync(path + "/finalize", new LifecycleChangeInput(scope.Version, "All visits resolved")))
            { Check(response.StatusCode == HttpStatusCode.OK, "Normal finalization succeeds after resolution"); var closed = (await response.Content.ReadFromJsonAsync<LifecycleScopeDto>())!; Check(closed.State == "Ended" && !closed.IsActive, "Final state ends only this practice location"); }
            Check((await doctor.GetFromJsonAsync<LifecycleScopeDto>($"/api/operations/locations/{otherPlace.Id}"))!.State == "Active", "Doctor keeps its other approved location");
            var history = (await doctor.GetFromJsonAsync<LifecyclePage<LifecycleHistoryDto>>(path + "/history"))!;
            Check(history.Items.Count == 2 && history.Items[0].Action == "Begin" && history.Items[1].Action == "Finalize", "Lifecycle history retains both reasoned transitions");
            using (var openapi = await guest.GetAsync("/openapi/v1.json")) Check(openapi.IsSuccessStatusCode && (await openapi.Content.ReadAsStringAsync()).Contains("/api/admin/operational-emergencies/{id}/decision", StringComparison.Ordinal), "OpenAPI publishes lifecycle and emergency decision contracts");
            await File.WriteAllTextAsync(Path.Combine(artifact, "report.json"), JsonSerializer.Serialize(new { Checks = checks, Count = checks.Count }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"P11_LIFECYCLE_HTTP_CHECKS:{checks.Count}; artifact:{artifact}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(artifact, "api.log"), log.ToString());
        }
    }
}
