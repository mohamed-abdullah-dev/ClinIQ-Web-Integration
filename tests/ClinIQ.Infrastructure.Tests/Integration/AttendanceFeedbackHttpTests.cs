using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.AttendanceFeedback;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Application.Features.Notifications;
using ClinIQ.Domain.Notifications;
using ClinIQ.Infrastructure.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AttendanceFeedbackHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealMobileFeedbackAndWebAdminReviewPreserveClaimsOwnershipCsrfAndAuditedFacts()
    {
        var schedule = new SchedulingTests(fixture); var (doctorId, place) = await schedule.Place();
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, schedule.Cairo.Zone).DateTime).AddDays(-1);
        var window = schedule.Cairo.Window(yesterday, new(12, 0), new(13, 0)).Value;
        fixture.Database.Clock.UtcNow = window.StartsAtUtc.AddMinutes(-10);
        var session = (await schedule.Extra(doctorId, place.Id, yesterday, 12, 13)).Value;
        var helper = new AppointmentTests(fixture); var patient = await helper.Patient(); var stranger = await helper.Patient();
        var booking = (await helper.Book(patient, session.Id)).Value.Appointment; fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        await using var db = fixture.Database.CreateContext();
        var doctorEmail = (await db.Users.SingleAsync(u => u.Id == doctorId)).Email!;
        var patientEmail = (await db.Users.SingleAsync(u => u.Id == patient)).Email!;
        var strangerEmail = (await db.Users.SingleAsync(u => u.Id == stranger)).Email!;
        var artifact = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p10-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifact);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot; start.Environment["Auth__Sessions__PermitLimit"] = "200";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); const string marker = "Now listening on: "; if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim())); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool ok, string label) { Assert.True(ok, label); checks.Add(label); }
        const string path = "/api/patients/me/attendance-feedback"; const string adminPath = "/api/admin/attendance-feedback";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var guest = Client(false); using var mobile = Client(false); using var other = Client(false); using var doctor = Client(true); using var admin = Client(true);
            await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail); await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            var refresh = await Mobile(mobile, patientEmail); await Mobile(other, strangerEmail);
            var installation = new RegisterPushDeviceInput(Guid.NewGuid(), SessionSecrets.Create(), "synthetic-http-" + Guid.NewGuid().ToString("N"), PushPlatform.Android);
            using (var response = await mobile.PostAsJsonAsync("/api/patients/me/push-devices", installation)) Check(response.StatusCode == HttpStatusCode.OK, "Mobile registers its own session-bound push device");
            using (var response = await admin.GetAsync("/api/patients/me/push-devices")) Check(response.StatusCode == HttpStatusCode.Forbidden, "Web Admin cannot register as a Patient Mobile device");
            using (var response = await other.DeleteAsync("/api/patients/me/push-devices/" + installation.InstallationId)) Check(response.StatusCode == HttpStatusCode.NotFound, "Another patient cannot detach the installation");
            using (var response = await mobile.GetAsync("/api/patients/me/push-devices")) { var body=await response.Content.ReadAsStringAsync(); Check(response.StatusCode == HttpStatusCode.OK && !body.Contains(installation.Token) && !body.Contains(installation.InstallationSecret), "Device listing never exposes token or installation secret"); }
            using (var response = await guest.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Feedback requires authentication");
            using (var response = await admin.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Admin Web cannot impersonate Patient Mobile");
            using (var response = await mobile.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient Mobile cannot inspect the Admin queue");
            using (var response = await doctor.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Doctor Web is not an Admin reviewer");
            var staffPath = $"/api/attendance/doctors/{session.DoctorProfileId}/sessions/{session.Id}/appointments";
            var row = (await doctor.GetFromJsonAsync<AttendanceQueue>(staffPath))!.Appointments.Items.Single(a => a.Id == booking.Id);
            using (var response = await doctor.PostAsJsonAsync(staffPath + $"/{row.Id}/no-show", new { confirmedPatientId = row.PatientId, version = row.Version })) Check(response.StatusCode == HttpStatusCode.OK, "Staff NoShow succeeds after the actual session end");
            var requests = (await mobile.GetFromJsonAsync<AttendancePage<FeedbackDto>>(path))!; var r = Assert.Single(requests.Items);
            Check(r.AppointmentId == booking.Id && r.Outcome == null && r.ReviewState == null && r.CanRespond, "NoShow creates an unanswered request visible without Push");
            Check(r.ExpiresAtUtc - r.CreatedAtUtc == TimeSpan.FromDays(7), "Patient sees the fixed seven-day deadline");
            using (var response = await admin.PostAsJsonAsync("/api/admin/feedback-deliveries/process", new { })) Check(response.StatusCode == HttpStatusCode.OK, "Admin can explicitly process a bounded notification batch");
            using (var response = await mobile.PostAsJsonAsync("/api/admin/feedback-deliveries/process", new { })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot process or inspect global delivery work");
            using (var response = await mobile.GetAsync(path + $"/{r.Id}")) Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Reading feedback is private and no-store");
            Check((await mobile.GetFromJsonAsync<FeedbackDto>(path + $"/{r.Id}"))!.Outcome == null, "Repeated reads and notification opens do not submit answers");
            using (var response = await other.GetAsync(path + $"/{r.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Another patient cannot read the request");
            using (var response = await other.PostAsJsonAsync(path + $"/{r.Id}/responses", new FeedbackAnswerInput(Guid.NewGuid(), PatientAttendanceOutcome.AttendedAndSeen, r.Version))) Check(response.StatusCode == HttpStatusCode.NotFound, "Another patient cannot answer the request");
            using (var response = await admin.GetAsync(adminPath + $"/{r.Id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Silence creates no Admin case");
            using (var response = await mobile.GetAsync(path + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Feedback history pages are bounded");
            using (var response = await mobile.PostAsJsonAsync(path + $"/{r.Id}/responses", new { submissionId = Guid.NewGuid(), outcome = 99, version = r.Version })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Exactly the three approved answers are accepted");
            var input = new FeedbackAnswerInput(Guid.NewGuid(), PatientAttendanceOutcome.AttendedAndSeen, r.Version);
            FeedbackAnswerReceipt receipt;
            using (var response = await mobile.PostAsJsonAsync(path + $"/{r.Id}/responses", input)) { Check(response.StatusCode == HttpStatusCode.OK, "One tap submits an answer and returns its receipt"); receipt = (await response.Content.ReadFromJsonAsync<FeedbackAnswerReceipt>())!; }
            Check(receipt.Current.ReviewState == AttendanceReviewState.Open && receipt.Submitted.ActorUserId == patient, "Attended claim opens one account-attributed review");
            Check((await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == booking.Id)).Status == AppointmentStatus.NoShow, "Patient claim never automatically changes operational attendance");
            using (var response = await mobile.PostAsJsonAsync(path + $"/{r.Id}/responses", input)) Check((await response.Content.ReadFromJsonAsync<FeedbackAnswerReceipt>())!.Replayed, "Network retry returns the same submission receipt");
            using (var response = await mobile.PutAsJsonAsync(path + $"/{r.Id}/response", input with { SubmissionId = Guid.NewGuid(), Outcome = PatientAttendanceOutcome.DidNotAttend })) Check(response.StatusCode == HttpStatusCode.Conflict, "A stale edit cannot overwrite the recorded answer");
            var editedInput = new FeedbackAnswerInput(Guid.NewGuid(), PatientAttendanceOutcome.AttendedNotSeen, receipt.Current.Version);
            using (var response = await mobile.PutAsJsonAsync(path + $"/{r.Id}/response", editedInput)) Check(response.StatusCode == HttpStatusCode.OK, "An explicit current-version edit is accepted");
            var detail = (await admin.GetFromJsonAsync<FeedbackReviewDetail>(adminPath + $"/{r.Id}"))!;
            Check(detail.Feedback.ResponseRevision == 2 && detail.Feedback.ReviewState == AttendanceReviewState.Open, "Admin sees the latest patient answer without losing the case");
            var responses = (await admin.GetFromJsonAsync<AttendancePage<FeedbackResponseDto>>(adminPath + $"/{r.Id}/responses"))!;
            Check(responses.Items.Count == 2 && responses.Items[0].Outcome == PatientAttendanceOutcome.AttendedAndSeen, "Both original and edited answers remain in history");
            var staff = (await admin.GetFromJsonAsync<AttendancePage<AttendanceHistory>>(adminPath + $"/{r.Id}/staff-history"))!;
            Check(staff.Items.Single().Action == "NoShow", "Admin can inspect the original staff audit event");
            var decision = new FeedbackReviewInput(detail.Feedback.Version, detail.AppointmentVersion, AttendanceReviewState.Resolved,
                PatientAttendanceOutcome.AttendedNotSeen, "Synthetic clarification confirms in-window arrival without consultation", window.StartsAtUtc.AddMinutes(5));
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{r.Id}/finalize", decision)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Admin decision requires Web CSRF");
            admin.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(admin));
            FeedbackReviewDetail closed;
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{r.Id}/finalize", decision)) { Check(response.StatusCode == HttpStatusCode.OK, "Admin finalizes using reviewed patient and booking versions"); closed = (await response.Content.ReadFromJsonAsync<FeedbackReviewDetail>())!; }
            Check(closed.Feedback.ReviewState == AttendanceReviewState.Resolved && closed.Attendance.Status == AppointmentStatus.AttendedNotSeen && closed.Attendance.ArrivedAtUtc == decision.ArrivedAtUtc && closed.Attendance.QueueNumber == 1, "Validated correction preserves real arrival and assigns an authoritative queue position");
            Check(closed.ReviewedByUserId == fixture.Admin.Id && closed.ReviewedResponseRevision == 2 && closed.ReviewedAppointmentVersion == detail.AppointmentVersion, "Final audit records reviewer, patient revision and the reviewed booking version");
            using (var response = await mobile.PutAsJsonAsync(path + $"/{r.Id}/response", new FeedbackAnswerInput(Guid.NewGuid(), PatientAttendanceOutcome.DidNotAttend, closed.Feedback.Version))) Check(response.StatusCode == HttpStatusCode.Conflict, "Finalized review rejects ordinary patient edits");
            Check(!closed.Feedback.CanRespond, "Final review removes the normal edit action");
            var history = (await admin.GetFromJsonAsync<AttendancePage<AttendanceHistory>>(adminPath + $"/{r.Id}/staff-history"))!;
            Check(history.Items.Count == 2 && history.Items[0].Action == "NoShow" && history.Items[1].ActorUserId == fixture.Admin.Id, "Original NoShow and Admin correction remain auditable");
            using (var response = await mobile.PostAsJsonAsync("/api/auth/mobile/logout", new { refreshToken=refresh })) Check(response.StatusCode == HttpStatusCode.NoContent, "Mobile logout succeeds");
            Check((await db.PushDevices.AsNoTracking().SingleAsync(d=>d.Id==installation.InstallationId)).UserId == null, "Logout atomically removes the account association and token material");
            await File.WriteAllTextAsync(Path.Combine(artifact, "report.json"), JsonSerializer.Serialize(new { Checks = checks, Count = checks.Count }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"P10_HTTP_CHECKS:{checks.Count}; artifact:{artifact}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(artifact, "api.log"), log.ToString());
        }
    }
    private static async Task<string> Mobile(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }
}
