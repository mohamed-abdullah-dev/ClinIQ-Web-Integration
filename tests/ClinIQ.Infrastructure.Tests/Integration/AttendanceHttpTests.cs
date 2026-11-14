using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AttendanceHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealWebAttendanceEnforcesDoctorFirstScopeCsrfFactsAndResponsibleDoctorLateAuthorization()
    {
        var schedule = new SchedulingTests(fixture); var (doctorId, place) = await schedule.Place(); var (otherDoctor, otherPlace) = await schedule.Place();
        var cairo = schedule.Cairo; var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, cairo.Zone).DateTime);
        var activeWindow = AttendanceHttpWindow.ForDate(cairo, today);
        Assert.True(DateTimeOffset.UtcNow.AddMinutes(2) < activeWindow.EndsAtUtc, "Run the real-clock arrival flow before today's final unambiguous session endpoint.");
        fixture.Database.Clock.UtcNow = activeWindow.StartsAtUtc.AddMinutes(-10);
        var active = (await schedule.Extra(doctorId, place.Id, today, activeWindow.StartsLocal.Hour, activeWindow.EndsLocal.Hour)).Value;
        // Extend only within valid local endpoints; the API still uses its real TimeProvider and business rules.
        active = (await schedule.Edit(doctorId, active, ClinIQ.Application.Features.Scheduling.SessionEditKind.Times, start: activeWindow.StartsLocal, end: activeWindow.EndsLocal)).Value;
        var otherSession = (await schedule.Extra(otherDoctor, otherPlace.Id, today, activeWindow.StartsLocal.Hour, activeWindow.EndsLocal.Hour)).Value;
        var helper = new AppointmentTests(fixture); var patient1 = await helper.Patient(); var patient2 = await helper.Patient();
        var first = (await helper.Book(patient1, active.Id)).Value.Appointment; var second = (await helper.Book(patient2, active.Id)).Value.Appointment;
        var yesterday = today.AddDays(-1); var endedWindow = cairo.Window(yesterday, new(12, 0), new(13, 0)).Value;
        fixture.Database.Clock.UtcNow = endedWindow.StartsAtUtc.AddMinutes(-10);
        var ended = (await schedule.Extra(doctorId, place.Id, yesterday, 12, 13)).Value;
        var absentBooking = (await helper.Book(patient1, ended.Id)).Value.Appointment;
        var staff = await fixture.UserAsync("Receptionist"); var invitations = new WorkInvitationTests(fixture);
        var assignment = (await invitations.Issue(doctorId, WorkInvitationKind.Receptionist, null, place.Id, staff.Email!)).Value; Assert.True((await invitations.Respond(staff.Id, assignment)).IsSuccess);
        var otherAssignment = (await invitations.Issue(otherDoctor, WorkInvitationKind.Receptionist, null, otherPlace.Id, staff.Email!)).Value; Assert.True((await invitations.Respond(staff.Id, otherAssignment)).IsSuccess);
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        await using var db = fixture.Database.CreateContext(); var doctorEmail = await db.Users.Where(u => u.Id == doctorId).Select(u => u.Email).SingleAsync(); var patientEmail = await db.Users.Where(u => u.Id == patient1).Select(u => u.Email).SingleAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p09-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development"; start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot; start.Environment["Auth__Sessions__PermitLimit"] = "200";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously); using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine(); var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        const string path = "/api/attendance/doctors"; var activePath = $"{path}/{active.DoctorProfileId}/sessions/{active.Id}/appointments"; var endedPath = $"{path}/{ended.DoctorProfileId}/sessions/{ended.Id}/appointments";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35)); using var guest = Client(false); using var reception = Client(true); using var doctor = Client(true); using var admin = Client(true); using var mobile = Client(false);
            await HttpSessionTestSupport.WebLoginAsync(reception, staff.Email!); await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail!); await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            using (var result = await mobile.PostAsJsonAsync("/api/auth/mobile/login", new { email = patientEmail, password = DoctorReviewFixture.Password })) { Assert.Equal(HttpStatusCode.OK, result.StatusCode); using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync()); mobile.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString()); }
            using (var result = await guest.GetAsync(path)) Check(result.StatusCode == HttpStatusCode.Unauthorized, "Attendance requires authentication");
            using (var result = await mobile.GetAsync(path)) Check(result.StatusCode == HttpStatusCode.Forbidden, "Patient Mobile cannot access staff attendance");
            using (var result = await admin.GetAsync(path)) Check(result.StatusCode == HttpStatusCode.Forbidden, "Admin alone does not receive clinical operational access");
            var doctors = (await reception.GetFromJsonAsync<AttendanceDoctor[]>(path))!; Check(doctors.Length == 2 && doctors.Any(d => d.Id == active.DoctorProfileId), "Reception chooses from its authorized doctors");
            var places = (await reception.GetFromJsonAsync<AttendancePlace[]>($"{path}/{active.DoctorProfileId}/locations"))!; Check(places.Length == 1 && places[0].Id == place.Id, "Selected doctor exposes only authorized places");
            using (var result = await reception.GetAsync($"{path}/{otherSession.DoctorProfileId}/sessions/{active.Id}/appointments")) Check(result.StatusCode == HttpStatusCode.NotFound, "A session cannot be read under a different selected doctor");
            using (var result = await reception.GetAsync($"{path}/{active.DoctorProfileId}/locations/{place.Id}/sessions?from={yesterday:yyyy-MM-dd}&until={today:yyyy-MM-dd}")) Check(result.StatusCode == HttpStatusCode.OK && result.Headers.CacheControl?.NoStore == true, "Date-filtered sessions are private and no-store");
            using (var result = await reception.GetAsync($"{path}/{active.DoctorProfileId}/locations/{place.Id}/sessions")) Check(result.StatusCode == HttpStatusCode.BadRequest, "Session dates must be explicit");
            using (var result = await reception.GetAsync(activePath + "?pageSize=101")) Check(result.StatusCode == HttpStatusCode.BadRequest, "Queue pages are bounded");
            var queue = (await reception.GetFromJsonAsync<AttendanceQueue>(activePath))!; var row = queue.Appointments.Items.Single(r => r.Id == first.Id); var waiting = queue.Appointments.Items.Single(r => r.Id == second.Id);
            Check(row.ContactName == "Synthetic Patient" && row.ContactPhone == "01012345678" && row.Facts.QueueNumber == null, "Patient identity is visible for explicit confirmation without a fabricated arrival");
            reception.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{row.Id}/check-in", Confirmation(row))) Check(result.StatusCode == HttpStatusCode.BadRequest, "Receptionist writes require Web CSRF");
            reception.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(reception));
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{row.Id}/check-in", new { confirmedPatientId = Guid.NewGuid(), version = row.Version })) Check(result.StatusCode == HttpStatusCode.Conflict, "Wrong patient confirmation is rejected before mutation");
            var arrived = await Act(reception, activePath, row, "check-in", Confirmation(row));
            Check(arrived.Changed && arrived.Appointment.Facts.QueueNumber == 1 && arrived.Appointment.Facts.Status == AppointmentStatus.CheckedIn, "Actual CheckIn establishes a server-time queue position");
            var repeat = await Act(reception, activePath, row, "check-in", Confirmation(row)); Check(!repeat.Changed && repeat.Appointment.Facts == arrived.Appointment.Facts, "Repeated old-version CheckIn is a no-op");
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{waiting.Id}/no-show", Confirmation(waiting))) Check(result.StatusCode == HttpStatusCode.Conflict, "NoShow cannot be recorded before the session ends");
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{arrived.Appointment.Id}/complete", Confirmation(arrived.Appointment))) Check(result.StatusCode == HttpStatusCode.BadRequest, "Completion requires its factual timestamp");
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{waiting.Id}/complete", new { confirmedPatientId = waiting.PatientId, version = waiting.Version, completedAtUtc = DateTimeOffset.UtcNow })) Check(result.StatusCode == HttpStatusCode.Conflict, "An unchecked-in patient cannot be marked Completed");
            var factual = DateTimeOffset.UtcNow;
            var completed = await Act(reception, activePath, arrived.Appointment, "complete", new { confirmedPatientId = arrived.Appointment.PatientId, version = arrived.Appointment.Version, completedAtUtc = factual });
            Check(completed.Appointment.Facts.CompletedAtUtc == factual && completed.Appointment.Facts.CompletionRecordedAtUtc >= factual, "Factual completion is distinct from server recording time");
            var corrected = await Act(reception, activePath, completed.Appointment, "correct", new { confirmedPatientId = completed.Appointment.PatientId, version = completed.Appointment.Version, status = (int)AppointmentStatus.CheckedIn, reason = "Mistaken terminal action; patient is actually waiting" });
            Check(corrected.Appointment.Facts.QueueNumber == 1 && corrected.Appointment.Facts.ArrivedAtUtc == arrived.Appointment.Facts.ArrivedAtUtc && corrected.Appointment.Facts.CompletedAtUtc == null, "Receptionist directly corrects completion without changing arrival or queue");
            using (var result = await reception.PostAsJsonAsync(activePath + $"/{row.Id}/correct", new { confirmedPatientId = row.PatientId, version = corrected.Appointment.Version, status = (int)AppointmentStatus.Cancelled, reason = "Not a free status editor" })) Check(result.StatusCode == HttpStatusCode.BadRequest, "Correction cannot manufacture Cancelled or Rescheduled status");
            var history = (await reception.GetFromJsonAsync<AttendancePage<AttendanceHistory>>(activePath + $"/{row.Id}/history"))!;
            Check(history.Items.Count == 3 && history.Items.Select(h => h.Revision).SequenceEqual(new[] { 1, 2, 3 }) && history.Items[2].Before.CompletedAtUtc == factual, "Append-only history preserves original completion and correction order");
            var own = (await mobile.GetFromJsonAsync<AppointmentDto>($"/api/patients/me/appointments/{row.Id}"))!; Check(own.Status == "CheckedIn" && own.QueueNumber == 1 && own.CompletedAtUtc == null, "Patient sees own corrected operational state without staff privileges");
            var waitingArrived = await Act(reception, activePath, waiting, "check-in", Confirmation(waiting));
            var notSeen = await Act(reception, activePath, waitingArrived.Appointment, "not-seen", new { confirmedPatientId = waitingArrived.Appointment.PatientId, version = waitingArrived.Appointment.Version, reason = "Arrived but left without consultation" });
            Check(notSeen.Appointment.Facts.Status == AppointmentStatus.AttendedNotSeen && notSeen.Appointment.Facts.ArrivedAtUtc.HasValue, "Attendance without consultation is distinct from NoShow");
            var endedQueue = (await reception.GetFromJsonAsync<AttendanceQueue>(endedPath))!; var absent = endedQueue.Appointments.Items.Single(r => r.Id == absentBooking.Id);
            absent = (await Act(reception, endedPath, absent, "no-show", Confirmation(absent))).Appointment;
            Check(absent.Facts.Status == AppointmentStatus.NoShow, "NoShow is recorded only for an unchecked-in patient after end");
            using (var result = await reception.PostAsJsonAsync(endedPath + $"/{absent.Id}/check-in", Confirmation(absent))) Check(result.StatusCode == HttpStatusCode.Conflict, "Ordinary CheckIn cannot bypass late authorization");
            var actualLate = DateTimeOffset.UtcNow;
            using (var result = await reception.PostAsJsonAsync(endedPath + $"/{absent.Id}/correct", new { confirmedPatientId = absent.PatientId, version = absent.Version, status = (int)AppointmentStatus.CheckedIn, arrivedAtUtc = actualLate, reason = "A real late arrival cannot masquerade as a correction" })) Check(result.StatusCode == HttpStatusCode.Conflict, "A new late arrival cannot use the direct correction route");
            using (var result = await reception.PostAsJsonAsync(endedPath + $"/{absent.Id}/late-check-in", new { confirmedPatientId = absent.PatientId, version = absent.Version, arrivedAtUtc = actualLate, reason = "Forged doctorApproved is ignored", doctorApproved = true })) Check(result.StatusCode == HttpStatusCode.Forbidden, "Receptionist cannot self-authorize as the doctor");
            var late = await Act(doctor, endedPath, absent, "late-check-in", new { confirmedPatientId = absent.PatientId, version = absent.Version, arrivedAtUtc = actualLate, reason = "Responsible doctor explicitly authorizes this late arrival" });
            Check(late.Appointment.Facts.LateAuthorizedByUserId == doctorId && late.Appointment.Facts.CheckInRecordedAtUtc >= actualLate, "Responsible doctor authorization records the real arrival and current recording time");
            queue = (await reception.GetFromJsonAsync<AttendanceQueue>(activePath))!; Check(queue.Session.AllocatedCount == 2 && queue.Session.LastQueueNumber == 2 && queue.Session.MaxCapacity == active.MaxCapacity, "Attendance and correction preserve capacity and per-session queue");
            var assignmentRow = (await invitations.Workforce(staff.Id, s => s.ListAssignmentsAsync(default))).Value.Single(a => a.LocationId == place.Id);
            Assert.True((await invitations.Workforce(doctorId, s => s.RevokeAssignmentAsync(assignmentRow.Id, assignmentRow.Version, "Assignment revoked", default))).IsSuccess);
            using (var result = await reception.GetAsync(activePath)) Check(result.StatusCode == HttpStatusCode.NotFound, "Revocation removes that scope from an otherwise-valid Web session");
            using (var result = await reception.GetAsync($"{path}/{otherSession.DoctorProfileId}/sessions/{otherSession.Id}/appointments")) Check(result.StatusCode == HttpStatusCode.OK, "The receptionist's other independent assignment remains available");
            using (var result = await guest.GetAsync("/openapi/v1.json")) Check(result.IsSuccessStatusCode && (await result.Content.ReadAsStringAsync()).Contains("/api/attendance/doctors/{doctorId}/sessions/{sessionId}/appointments/{id}/late-check-in", StringComparison.Ordinal), "OpenAPI contains scoped attendance and late authorization routes");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks })); output.WriteLine($"P09_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            object Confirmation(AttendanceAppointment a) => new { confirmedPatientId = a.PatientId, version = a.Version };
            async Task<AttendanceWriteResult> Act(HttpClient client, string scope, AttendanceAppointment a, string action, object body) { using var response = await client.PostAsJsonAsync(scope + $"/{a.Id}/{action}", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Check(response.Headers.CacheControl?.NoStore == true, "Successful attendance write is no-store: " + action); return (await response.Content.ReadFromJsonAsync<AttendanceWriteResult>())!; }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
}
