using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Application.Features.Directory;
using ClinIQ.Application.Features.Scheduling;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PublicDirectoryHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task GuestsCanDiscoverRealAvailabilityButCannotAccessPrivateWorkflows()
    {
        var setup = new SchedulingTests(fixture); var (doctorId, place) = await setup.Place();
        var secondPlace = await setup.SecondPlace(doctorId);
        var (_, clinic, branch) = await new PremisesCorrectionTests(fixture).Branch();
        fixture.Database.Clock.UtcNow = DateTimeOffset.UtcNow;
        var first = (await setup.Extra(doctorId, place.Id, setup.Tomorrow, 18, 20)).Value;
        await setup.Extra(doctorId, secondPlace.Id, setup.Tomorrow.AddDays(1), 18, 20);
        await using var db = fixture.Database.CreateContext();
        var email = await db.Users.Where(u => u.Id == doctorId).Select(u => u.Email).SingleAsync();
        var beforeSessions = await db.AuthSessions.CountAsync();
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p07-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection; start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            lock (log) log.AppendLine(e.Data);
            const string marker = "Now listening on: ";
            if (e.Data?.Contains(marker, StringComparison.Ordinal) == true) listening.TrySetResult(new Uri(e.Data[(e.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim()));
        };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>(); void Check(bool value, string label) { Assert.True(value, label); checks.Add(label); }
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(35));
            using var guest = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            using var response = await guest.GetAsync("/api/public/doctors");
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Guest search succeeds and is no-store");
            Check(!response.Headers.Contains("Set-Cookie"), "Guest browsing creates no authentication or CSRF cookie");
            var page = (await response.Content.ReadFromJsonAsync<DirectoryPage<DoctorOption>>())!;
            Check(page.TotalCount == 2 && page.Items.Count == 2, "Two locations produce two options");
            Check(page.Items[0].Place.Id == place.Id && page.Items[0].NextSession.Id == first.Id && page.Items[0].Place.ConsultationFee == 100, "Guest gets real location, session and price");
            Check(page.Items.All(x => x.DistanceKm == null && x.Doctor.ReviewAverage == null && x.Doctor.ReviewCount == 0), "Missing GPS and unimplemented reviews stay explicit");
            var json = await response.Content.ReadAsStringAsync();
            Check(!json.Contains(email!, StringComparison.OrdinalIgnoreCase) && !json.Contains("userId", StringComparison.OrdinalIgnoreCase) && !json.Contains("evidence", StringComparison.OrdinalIgnoreCase) && !json.Contains("version", StringComparison.OrdinalIgnoreCase), "Public DTO omits account and evidence fields");
            await Status($"/api/public/doctors/{first.DoctorProfileId}", HttpStatusCode.OK, "Doctor public profile works");
            await Status($"/api/public/locations/{place.Id}", HttpStatusCode.OK, "Location public profile works");
            await Status($"/api/public/sessions/{first.Id}", HttpStatusCode.OK, "Bookable session detail works");
            await Status($"/api/public/clinics/{clinic}", HttpStatusCode.OK, "Approved clinic profile works");
            await Status($"/api/public/clinics/{clinic}/branches/{branch.Id}", HttpStatusCode.OK, "Approved branch profile works without inventing availability");
            await Status($"/api/public/clinics/{Guid.NewGuid()}/branches/{branch.Id}", HttpStatusCode.NotFound, "Foreign clinic branch pair is hidden");
            foreach (var invalid in new[] { "page=0", "pageSize=101", "minFee=200&maxFee=100", "minFee=1.001", "sort=99", "sort=4", "latitude=30", "latitude=91&longitude=31", "maxDistanceKm=1", "latitude=0&longitude=0&maxDistanceKm=NaN", $"from={setup.Cairo.Today.AddDays(-1):yyyy-MM-dd}", $"until={setup.Cairo.Today.AddDays(28):yyyy-MM-dd}" })
                await Status("/api/public/doctors?" + invalid, HttpStatusCode.BadRequest, "Invalid search rejected: " + invalid);
            var empty = (await guest.GetFromJsonAsync<DirectoryPage<DoctorOption>>("/api/public/doctors?area=UnmatchedArea"))!;
            Check(empty.TotalCount == 0 && empty.Items.Count == 0, "No matches return an empty page without fallback");
            var cheaper = (await guest.GetFromJsonAsync<DirectoryPage<DoctorOption>>("/api/public/doctors?maxFee=100&pageSize=1"))!;
            Check(cheaper.TotalCount == 1 && cheaper.Items[0].Place.Id == place.Id, "Price filters and pagination execute through HTTP");
            await Status("/api/public/doctors?latitude=30&longitude=31&sort=4", HttpStatusCode.OK, "Distance order works with explicit user coordinates");
            await Status("/api/doctors/me/scheduling/rules", HttpStatusCode.Unauthorized, "Guest cannot read private scheduling");
            using (var write = await guest.PostAsJsonAsync("/api/public/doctors", new { })) Check(write.StatusCode == HttpStatusCode.MethodNotAllowed, "Public directory exposes no write action");
            Check(await db.AuthSessions.CountAsync() == beforeSessions, "Public reads issue no persistent login session");
            await setup.Edit(doctorId, first, SessionEditKind.Pause);
            await Status($"/api/public/sessions/{first.Id}", HttpStatusCode.NotFound, "Paused session is immediately hidden on subsequent HTTP read");
            var changed = (await guest.GetFromJsonAsync<DirectoryPage<DoctorOption>>("/api/public/doctors"))!;
            Check(changed.TotalCount == 1 && changed.Items[0].Place.Id == secondPlace.Id, "Search immediately reflects live booking controls");
            using (var openapi = await guest.GetAsync("/openapi/v1.json")) Check(openapi.IsSuccessStatusCode && (await openapi.Content.ReadAsStringAsync()).Contains("/api/public/clinics/{clinicId}/branches/{branchId}", StringComparison.Ordinal), "OpenAPI contains public routes");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"P07_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");
            async Task Status(string path, HttpStatusCode expected, string label)
            { using var result = await guest.GetAsync(path); Check(result.StatusCode == expected, label + $" (actual {(int)result.StatusCode})"); }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }
    }
}
