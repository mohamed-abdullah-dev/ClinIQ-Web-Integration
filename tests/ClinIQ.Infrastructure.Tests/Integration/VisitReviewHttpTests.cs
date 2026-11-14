using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Attendance;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.VisitReviews;
using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.VisitReviews;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class VisitReviewHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealMobileReviewPublicAggregateAndAdminModerationEnforceOwnershipVersionsCsrfAndCorrection()
    {
        var schedule = new SchedulingTests(fixture); var (doctorId, place) = await schedule.Place();
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, schedule.Cairo.Zone).DateTime).AddDays(-1);
        var window = schedule.Cairo.Window(yesterday, new(12, 0), new(13, 0)).Value;
        fixture.Database.Clock.UtcNow = window.StartsAtUtc.AddMinutes(-10);
        var session = (await schedule.Extra(doctorId, place.Id, yesterday, 12, 13)).Value;
        var helper = new AppointmentTests(fixture); var patient = await helper.Patient(); var stranger = await helper.Patient();
        var booking = (await helper.Book(patient, session.Id)).Value.Appointment;
        var attendance = new AttendanceTests(fixture);
        var scenario = new AttendanceTests.Scenario(doctorId, doctorId, session, [], [patient], null, null);
        fixture.Database.Clock.UtcNow = window.StartsAtUtc;
        var row = await attendance.Current(doctorId, scenario, booking.Id);
        row = (await attendance.Write(doctorId, scenario, row, AttendanceAction.CheckIn)).Value.Appointment;
        fixture.Database.Clock.UtcNow = window.EndsAtUtc;
        Assert.True((await attendance.Write(doctorId, scenario, row, AttendanceAction.Complete, completed: window.StartsAtUtc.AddMinutes(10))).IsSuccess);
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        await using var db = fixture.Database.CreateContext();
        var doctorEmail = (await db.Users.SingleAsync(u => u.Id == doctorId)).Email!;
        var patientEmail = (await db.Users.SingleAsync(u => u.Id == patient)).Email!;
        var strangerEmail = (await db.Users.SingleAsync(u => u.Id == stranger)).Email!;
        var artifact = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p11-http", Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(artifact);
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
        var path = $"/api/patients/me/appointments/{booking.Id}/review";
        var publicPath = $"/api/public/doctors/{session.DoctorProfileId}/reviews";
        const string adminPath = "/api/admin/visit-reviews";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var guest = Client(false); using var mobile = Client(false); using var other = Client(false); using var doctor = Client(true); using var admin = Client(true);
            await HttpSessionTestSupport.WebLoginAsync(doctor, doctorEmail); await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            await Mobile(mobile, patientEmail); await Mobile(other, strangerEmail);
            using (var response = await guest.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Own review requires login");
            using (var response = await admin.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Web Admin cannot impersonate Patient Mobile");
            using (var response = await other.PostAsJsonAsync(path, new WriteVisitReviewInput(5, null))) Check(response.StatusCode == HttpStatusCode.NotFound, "Another patient cannot review this booking");
            using (var response = await mobile.PostAsJsonAsync(path, new WriteVisitReviewInput(6, null))) Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid stars are rejected");
            VisitReviewWriteResult receipt;
            using (var response = await mobile.PostAsJsonAsync(path, new WriteVisitReviewInput(1, "Synthetic feedback")))
            {
                Check(response.StatusCode == HttpStatusCode.Created && response.Headers.Location?.ToString().EndsWith(path, StringComparison.Ordinal) == true, "First review returns 201 and own GET location");
                receipt = (await response.Content.ReadFromJsonAsync<VisitReviewWriteResult>())!;
            }
            using (var response = await mobile.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Own review is readable and no-store");
            using (var response = await other.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.NotFound, "Other patient cannot read own-review details");
            using (var response = await mobile.PostAsJsonAsync(path, new WriteVisitReviewInput(1, "Synthetic feedback"))) Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<VisitReviewWriteResult>())!.Replayed, "Identical retry returns receipt without duplicate");
            using (var response = await mobile.PostAsJsonAsync(path, new WriteVisitReviewInput(5, null))) Check(response.StatusCode == HttpStatusCode.Conflict, "Second different create cannot overwrite review");
            using (var response = await guest.GetAsync(publicPath))
            {
                var json = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<VisitReviewPage<PublicVisitReview>>())!.TotalCount == 1, "One-star review is immediately public");
                Check(!json.Contains("patient", StringComparison.OrdinalIgnoreCase) && !json.Contains("appointment", StringComparison.OrdinalIgnoreCase) && !json.Contains(patient.ToString()), "Public review omits patient and booking identity");
            }
            using (var response = await guest.GetAsync(publicPath + "?pageSize=101")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Public page size is bounded");
            using (var response = await mobile.PutAsJsonAsync(path, new WriteVisitReviewInput(4, "Edited feedback", receipt.Review.Version)))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Explicit current-version edit succeeds");
                var edited = (await response.Content.ReadFromJsonAsync<VisitReviewWriteResult>())!;
                using var stale = await mobile.PutAsJsonAsync(path, new WriteVisitReviewInput(5, null, receipt.Review.Version));
                Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale review edit is rejected"); receipt = edited;
            }
            var reviewId = receipt.Review.Id;
            using (var response = await doctor.PostAsJsonAsync(adminPath + $"/{reviewId}/moderation", new ModerateVisitReviewInput(true, ReviewModerationCategory.Abusive, "Synthetic reason", receipt.Review.Version))) Check(response.StatusCode == HttpStatusCode.Forbidden, "Doctor cannot moderate patient feedback");
            using (var response = await mobile.GetAsync(adminPath)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Patient cannot inspect global Admin reviews");
            var moderation = new ModerateVisitReviewInput(true, ReviewModerationCategory.PrivateInformation, "Synthetic private information", receipt.Review.Version);
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{reviewId}/moderation", moderation)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Moderation requires Web CSRF");
            admin.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(admin));
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{reviewId}/moderation", moderation with { Reason = "" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Moderation requires recorded reason");
            VisitReviewDto hidden;
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{reviewId}/moderation", moderation)) { Check(response.StatusCode == HttpStatusCode.OK, "Admin hides with privacy reason"); hidden = (await response.Content.ReadFromJsonAsync<VisitReviewDto>())!; }
            Check((await guest.GetFromJsonAsync<VisitReviewPage<PublicVisitReview>>(publicPath))!.TotalCount == 0, "Hidden review is removed from public listing");
            Check((await guest.GetFromJsonAsync<PublicDoctorProfile>($"/api/public/doctors/{session.DoctorProfileId}"))!.Doctor.ReviewCount == 0, "Hidden review is excluded from doctor aggregate");
            using (var response = await mobile.PutAsJsonAsync(path, new WriteVisitReviewInput(5, "Private text removed", hidden.Version))) { var edit = (await response.Content.ReadFromJsonAsync<VisitReviewWriteResult>())!; Check(response.StatusCode == HttpStatusCode.OK && edit.Review.Hidden, "Patient edit preserves Admin hide"); hidden = edit.Review; }
            using (var response = await admin.PostAsJsonAsync(adminPath + $"/{reviewId}/moderation", new ModerateVisitReviewInput(false, null, "Synthetic restoration after edit", hidden.Version))) Check(response.StatusCode == HttpStatusCode.OK, "Admin restores with recorded reason");
            Check((await guest.GetFromJsonAsync<PublicDoctorProfile>($"/api/public/doctors/{session.DoctorProfileId}"))!.Doctor.ReviewAverage == 5, "Restored current stars contribute to aggregate");
            var history = (await admin.GetFromJsonAsync<VisitReviewPage<VisitReviewAuditDto>>(adminPath + $"/{reviewId}/history"))!;
            Check(history.Items.Count == 5 && history.Items[0].Action == "Create" && history.Items[^1].Action == "Restore", "All creation edits and moderation remain in history");
            var staffPath = $"/api/attendance/doctors/{session.DoctorProfileId}/sessions/{session.Id}/appointments";
            var current = (await doctor.GetFromJsonAsync<AttendanceQueue>(staffPath))!.Appointments.Items.Single(a => a.Id == booking.Id);
            using (var response = await doctor.PostAsJsonAsync(staffPath + $"/{booking.Id}/correct", new { confirmedPatientId = current.PatientId, version = current.Version, status = AppointmentStatus.CheckedIn, reason = "Synthetic correction of mistaken completion" }))
                Check(response.StatusCode == HttpStatusCode.OK, "Staff correction away from Completed succeeds");
            Check((await guest.GetFromJsonAsync<VisitReviewPage<PublicVisitReview>>(publicPath))!.TotalCount == 0, "Corrected completion suspends publication");
            Check(!(await mobile.GetFromJsonAsync<VisitReviewDto>(path))!.Eligible, "Patient sees preserved but currently ineligible review");
            await File.WriteAllTextAsync(Path.Combine(artifact, "report.json"), JsonSerializer.Serialize(new { Checks = checks, Count = checks.Count }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"P11_HTTP_CHECKS:{checks.Count}; artifact:{artifact}");
            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(artifact, "api.log"), log.ToString());
        }
    }
    private static async Task Mobile(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString());
    }
}
