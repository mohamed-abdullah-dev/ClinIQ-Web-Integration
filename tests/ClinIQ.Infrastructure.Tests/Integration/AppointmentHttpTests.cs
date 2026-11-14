using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Scheduling;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class AppointmentHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealMobileBookingRescheduleCancellationAndWebDoctorDisruption()
    {
        var helper = new AppointmentTests(fixture); var schedule = new SchedulingTests(fixture);
        var (doctor, place) = await schedule.Place(); var secondPlace = await schedule.SecondPlace(doctor);
        var (otherDoctor, otherPlace) = await schedule.Place();
        var patient = await helper.Patient(); var otherPatient = await helper.Patient(); var incomplete = await fixture.UserAsync("Patient");
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var first = (await schedule.Extra(doctor, place.Id, schedule.Tomorrow, 18, 20)).Value;
        var next = (await schedule.Extra(doctor, secondPlace.Id, schedule.Tomorrow.AddDays(1), 18, 20)).Value;
        var overlap = (await schedule.Extra(otherDoctor, otherPlace.Id, schedule.Tomorrow, 19, 21)).Value;
        var lastPlace = (await schedule.Extra(doctor, place.Id, schedule.Tomorrow.AddDays(2), 18, 20, 1)).Value;
        await using var db = fixture.Database.CreateContext();
        var doctorEmail = await db.Users.Where(u => u.Id == doctor).Select(u => u.Email).SingleAsync();
        var patientEmail = await db.Users.Where(u => u.Id == patient).Select(u => u.Email).SingleAsync();
        var otherEmail = await db.Users.Where(u => u.Id == otherPatient).Select(u => u.Email).SingleAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p08-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(artifacts);
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
        var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        const string path = "/api/patients/me/appointments";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var guest = Client(false); using var mobile = Client(false); using var other = Client(false); using var missing = Client(false); using var web = Client(true); using var admin = Client(true);
            await Login(mobile, patientEmail!); await Login(other, otherEmail!); await Login(missing, incomplete.Email!);
            await HttpSessionTestSupport.WebLoginAsync(web, doctorEmail!); await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            using (var result = await guest.PostAsJsonAsync(path + "/preview", new { sessionId = first.Id })) Check(result.StatusCode == HttpStatusCode.Unauthorized, "Guest cannot preview or confirm private booking");
            using (var result = await web.GetAsync(path)) Check(result.StatusCode == HttpStatusCode.Forbidden, "Doctor Web session has no Patient booking power");
            using (var result = await missing.PostAsJsonAsync(path + "/preview", new { sessionId = first.Id })) Check(result.StatusCode == HttpStatusCode.NotFound, "Patient must complete contact profile before booking");
            var preview = await Preview(mobile, first.Id);
            Check(preview.Selection.Fee == 100 && preview.Selection.ContactName == "Synthetic Patient" && preview.Selection.ContactPhone == "01012345678", "Preview shows current price and own contact for confirmation");
            using (var result = await mobile.PostAsJsonAsync(path, new { sessionId = first.Id, quoteToken = preview.QuoteToken, requestId = Guid.NewGuid() })) Check(result.StatusCode == HttpStatusCode.BadRequest, "Omitted price/contact acceptance cannot default to consent");
            using (var result = await other.PostAsJsonAsync(path, AppointmentTests.Accept(preview))) Check(result.StatusCode == HttpStatusCode.Conflict, "Quote is bound to its patient");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DoctorPracticeLocations SET ConsultationFee=125 WHERE Id={place.Id}");
            using (var result = await mobile.PostAsJsonAsync(path, AppointmentTests.Accept(preview))) Check(result.StatusCode == HttpStatusCode.Conflict, "Stale price requires another preview");
            preview = await Preview(mobile, first.Id); var input = AppointmentTests.Accept(preview);
            AppointmentDto booking;
            using (var result = await mobile.PostAsJsonAsync(path, new { input.SessionId, input.QuoteToken, input.RequestId, input.AcceptPrice, input.ConfirmContact, input.AcknowledgeOverlap, patientId = Guid.NewGuid(), userId = otherPatient }))
            {
                Check(result.StatusCode == HttpStatusCode.Created && result.Headers.Location != null && result.Headers.CacheControl?.NoStore == true, "Booking returns private 201 and Location");
                booking = (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Appointment;
            }
            Check((await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == booking.Id)).PatientUserId == patient, "Forged user/patient IDs cannot switch the beneficiary");
            using (var result = await mobile.PostAsJsonAsync(path, input)) Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Replayed, "Same request replays with 200 and no duplicate");
            using (var result = await mobile.PostAsJsonAsync(path, input with { RequestId = Guid.NewGuid() })) Check(result.StatusCode == HttpStatusCode.Conflict, "Another request ID cannot duplicate an active reservation");
            using (var result = await other.GetAsync(path + "/" + booking.Id)) Check(result.StatusCode == HttpStatusCode.NotFound, "Another patient cannot read the booking");
            using (var result = await other.PostAsJsonAsync(path + "/" + booking.Id + "/cancel", new { requestId = Guid.NewGuid(), reason = "Foreign" })) Check(result.StatusCode == HttpStatusCode.NotFound, "Another patient cannot cancel the booking");
            var overlapPreview = await Preview(mobile, overlap.Id); Check(overlapPreview.Overlaps.Count == 1 && overlapPreview.Warning != null, "Overlapping doctor session has an explicit warning");
            using (var result = await mobile.PostAsJsonAsync(path, AppointmentTests.Accept(overlapPreview))) Check(result.StatusCode == HttpStatusCode.Conflict, "Overlap requires patient acknowledgement");
            AppointmentDto overlapping;
            using (var result = await mobile.PostAsJsonAsync(path, AppointmentTests.Accept(overlapPreview, true))) { Check(result.StatusCode == HttpStatusCode.Created, "Acknowledged different-doctor overlap is allowed"); overlapping = (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Appointment; }
            using (var result = await mobile.PostAsJsonAsync(path + "/" + booking.Id + "/reschedule/preview", new { sessionId = overlap.Id })) Check(result.StatusCode == HttpStatusCode.BadRequest, "Reschedule cannot change doctor");
            using (var result = await mobile.PostAsJsonAsync(path + "/" + booking.Id + "/reschedule/preview", new { sessionId = first.Id })) Check(result.StatusCode == HttpStatusCode.BadRequest, "Reschedule cannot create a replacement in the same session");
            BookingPreview transfer;
            using (var result = await mobile.PostAsJsonAsync(path + "/" + booking.Id + "/reschedule/preview", new { sessionId = next.Id })) { Check(result.StatusCode == HttpStatusCode.OK, "Same-doctor another-location preview succeeds"); transfer = (await result.Content.ReadFromJsonAsync<BookingPreview>())!; }
            var transferInput = AppointmentTests.Accept(transfer); AppointmentDto replacement;
            using (var result = await mobile.PostAsJsonAsync(path + "/" + booking.Id + "/reschedule", transferInput)) { Check(result.StatusCode == HttpStatusCode.Created, "Reschedule creates a linked replacement"); replacement = (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Appointment; }
            var source = (await mobile.GetFromJsonAsync<AppointmentDto>(path + "/" + booking.Id))!;
            Check(source.Status == "Rescheduled" && source.ReplacedByAppointmentId == replacement.Id && replacement.ReplacesAppointmentId == booking.Id && source.Selection.Fee == 125 && replacement.Selection.Fee == 200, "Linked history preserves separately agreed fees");
            using (var result = await mobile.PostAsJsonAsync(path + "/" + booking.Id + "/reschedule", transferInput)) Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Replayed, "Reschedule replay does not move twice");
            var cancel = new { requestId = Guid.NewGuid(), reason = "Cannot attend" };
            using (var result = await mobile.PostAsJsonAsync(path + "/" + overlapping.Id + "/cancel", cancel)) Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Appointment.Status == "Cancelled", "Patient can cancel own unstarted booking");
            using (var result = await mobile.PostAsJsonAsync(path + "/" + overlapping.Id + "/cancel", cancel)) Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadFromJsonAsync<AppointmentOperationResult>())!.Replayed, "Cancellation replay releases once");
            var mine = (await mobile.GetFromJsonAsync<AppointmentPage>(path + "?pageSize=2"))!; Check(mine.TotalCount == 3 && mine.Items.Count == 2, "MyAppointments includes retained history with bounded pages");
            using (var result = await mobile.GetAsync(path + "?pageSize=101")) Check(result.StatusCode == HttpStatusCode.BadRequest, "MyAppointments rejects unbounded pagination");
            var session = (await web.GetFromJsonAsync<SessionDto>($"/api/doctors/me/scheduling/sessions/{next.Id}"))!;
            web.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var result = await web.PostAsJsonAsync($"/api/doctors/me/scheduling/sessions/{next.Id}/cancel", new { version = session.Version, reason = "Doctor illness" })) Check(result.StatusCode == HttpStatusCode.BadRequest, "Doctor cancellation still requires Web CSRF");
            web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(web));
            using (var result = await web.PostAsJsonAsync($"/api/doctors/me/scheduling/sessions/{next.Id}/cancel", new { version = session.Version, reason = "Doctor illness" })) Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadFromJsonAsync<SessionDto>())!.AllocatedCount == 0, "Responsible doctor resolves the session and remaining reservations atomically");
            Check((await mobile.GetFromJsonAsync<AppointmentDto>(path + "/" + replacement.Id))!.Status == "Cancelled", "Affected patient sees committed doctor cancellation");
            var raceA = AppointmentTests.Accept(await Preview(mobile, lastPlace.Id)); var raceB = AppointmentTests.Accept(await Preview(other, lastPlace.Id));
            var races = await Task.WhenAll(mobile.PostAsJsonAsync(path, raceA), other.PostAsJsonAsync(path, raceB));
            try { Check(races.Count(r => r.StatusCode == HttpStatusCode.Created) == 1 && races.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Concurrent HTTP clients cannot oversell the last place"); }
            finally { foreach (var result in races) result.Dispose(); }
            using (var result = await mobile.PostAsJsonAsync("/api/admin/booking-notices/retry", new { })) Check(result.StatusCode == HttpStatusCode.Forbidden, "Patient Mobile cannot run Admin notification retry");
            using (var result = await admin.PostAsJsonAsync("/api/admin/booking-notices/retry", new { })) Check(result.StatusCode == HttpStatusCode.OK, "Admin Web has explicit bounded retry action");
            using (var result = await guest.GetAsync("/openapi/v1.json")) Check(result.IsSuccessStatusCode && (await result.Content.ReadAsStringAsync()).Contains("/api/patients/me/appointments/{id}/reschedule", StringComparison.Ordinal), "OpenAPI publishes booking and reschedule contracts");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks })); output.WriteLine($"P08_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task Login(HttpClient client, string email) { using var result = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password }); Assert.Equal(HttpStatusCode.OK, result.StatusCode); using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync()); client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString()); }
            async Task<BookingPreview> Preview(HttpClient client, Guid sessionId) { using var result = await client.PostAsJsonAsync(path + "/preview", new { sessionId }); Assert.Equal(HttpStatusCode.OK, result.StatusCode); return (await result.Content.ReadFromJsonAsync<BookingPreview>())!; }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString()); }
    }
}
