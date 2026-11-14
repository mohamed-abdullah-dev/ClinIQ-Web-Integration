using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinIQ.Domain.Specialties;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SpecialtyAdministrationHttpTests(DoctorReviewFixture fixture, ITestOutputHelper output)
    : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task RealHttpProtectsCatalogMutationsAndPreservesPublicLookupAndAudit()
    {
        var patient = await fixture.UserAsync("Patient");
        await fixture.DoctorAsync(); // A pending submission is already a reference that prevents deletion.
        var artifacts = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "specialty-admin-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var apiRoot = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(apiRoot, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection;
        start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["Auth__Sessions__PermitLimit"] = "100";
        var log = new StringBuilder(); var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            lock (log) log.AppendLine(args.Data);
            const string marker = "Now listening on: ";
            if (args.Data?.Contains(marker, StringComparison.Ordinal) == true)
                listening.TrySetResult(new Uri(args.Data[(args.Data.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim()));
        };
        process.ErrorDataReceived += (_, args) => { lock (log) log.AppendLine(args.Data); };
        Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var checks = new List<string>();
        void Check(bool valid, string label) { Assert.True(valid, label); checks.Add(label); }
        const string path = "/api/admin/specialties";
        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var anonymous = Client(false); using var admin = Client(true); using var ordinary = Client(false); using var web = Client(true);
            using (var response = await anonymous.GetAsync("/api/specialties")) Check(response.StatusCode == HttpStatusCode.OK, "Public lookup remains anonymous");
            using (var response = await anonymous.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Unauthorized && NoStore(response), "Anonymous admin list returns no-store 401");
            using (var response = await anonymous.PostAsJsonAsync(path, new { nameAr = "جراحة", nameEn = "Surgery" })) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous creation is rejected");
            ordinary.DefaultRequestHeaders.Authorization = new("Bearer", await Login(ordinary, patient.Email!));
            using (var response = await ordinary.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.Forbidden && NoStore(response), "Patient cannot read admin catalog metadata");
            using (var response = await ordinary.PostAsJsonAsync(path, new { nameAr = "جراحة", nameEn = "Surgery", role = "Admin" })) Check(response.StatusCode == HttpStatusCode.Forbidden, "Injected role cannot permit patient creation");
            await HttpSessionTestSupport.WebLoginAsync(admin,fixture.Admin.Email!);
            using (var response = await admin.GetAsync(path)) Check(response.StatusCode == HttpStatusCode.OK && NoStore(response), "Admin reads a private catalog");
            using (var response = await admin.PostAsJsonAsync(path, new { nameAr = "", nameEn = "Valid" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Missing display name returns 400");
            var suffix = Guid.NewGuid().ToString("N"); var forged = Guid.NewGuid();
            Guid id; string version;
            using (var response = await admin.PostAsJsonAsync(path, new
            {
                nameAr = "  أطفال " + suffix + "  ", nameEn = "  Pediatrics " + suffix + "  ",
                id = forged, normalizedNameAr = "forged", normalizedNameEn = "forged", actorUserId = patient.Id, createdByUserId = patient.Id
            }))
            {
                Check(response.StatusCode == HttpStatusCode.Created && NoStore(response), "Admin creation returns private 201");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                id = json.RootElement.GetProperty("id").GetGuid(); version = json.RootElement.GetProperty("version").GetString()!;
                Check(id != forged && id != Guid.Empty, "Specialty identity comes from the server");
                Check(json.RootElement.GetProperty("nameAr").GetString() == "أطفال " + suffix, "Display names retain spelling after trimming");
                Check(response.Headers.Location?.ToString().EndsWith(id.ToString(), StringComparison.Ordinal) == true, "201 includes the specialty GET location");
            }
            using (var response = await admin.PostAsJsonAsync(path, new { nameAr = "اطفال " + suffix, nameEn = "Different " + suffix }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "Arabic Alef variants cannot create a duplicate");
            using (var response = await admin.PostAsJsonAsync(path, new { nameAr = "مختلف " + suffix, nameEn = "PEDIATRICS " + suffix }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "English case variants cannot create a duplicate");
            using (var response = await admin.PutAsJsonAsync($"{path}/{id}", new { nameAr = "تعديل", nameEn = "Edited", version = "bad" }))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed edit version returns 400");
            using (var response = await admin.PutAsJsonAsync($"{path}/{id}", new { nameAr = "تعديل", nameEn = "Edited", version = "AAAAAAAAAAA=" }))
                Check(response.StatusCode == HttpStatusCode.Conflict, "Stale edit version returns 409");
            var renames = await Task.WhenAll(
                admin.PutAsJsonAsync($"{path}/{id}", new { nameAr = "أول " + suffix, nameEn = "First " + suffix, version }),
                admin.PutAsJsonAsync($"{path}/{id}", new { nameAr = "ثاني " + suffix, nameEn = "Second " + suffix, version }));
            try { Check(renames.Count(response => response.StatusCode == HttpStatusCode.OK) == 1 && renames.Count(response => response.StatusCode == HttpStatusCode.Conflict) == 1, "Concurrent edits have one winner and one 409"); }
            finally { foreach (var response in renames) response.Dispose(); }
            using (var response = await admin.DeleteAsync($"{path}/{id}?version={Uri.EscapeDataString(version)}")) Check(response.StatusCode == HttpStatusCode.Conflict, "Old version cannot delete a renamed specialty");
            version = await Version(id);
            using (var response = await admin.DeleteAsync($"{path}/{id}?version={Uri.EscapeDataString(version)}")) Check(response.StatusCode == HttpStatusCode.NoContent && NoStore(response), "Unused specialty deletion returns private 204");
            using (var response = await admin.GetAsync($"{path}/{id}")) Check(response.StatusCode == HttpStatusCode.NotFound, "Deleted specialty no longer appears as a catalog row");
            var linkedVersion = await Version(fixture.SpecialtyId);
            using (var response = await admin.DeleteAsync($"{path}/{fixture.SpecialtyId}?version={Uri.EscapeDataString(linkedVersion)}")) Check(response.StatusCode == HttpStatusCode.Conflict, "Pending doctor registration prevents deletion");
            using (var response = await admin.PutAsJsonAsync($"{path}/{fixture.SpecialtyId}", new { nameAr = "مرتبط " + suffix, nameEn = "Linked " + suffix, version = linkedVersion }))
                Check(response.StatusCode == HttpStatusCode.OK, "Referenced specialty can be renamed with its identity preserved");
            using (var response = await anonymous.GetAsync("/api/specialties"))
            {
                var text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text);
                Check(json.RootElement.EnumerateArray().Any(row => row.GetProperty("id").GetGuid() == fixture.SpecialtyId && row.GetProperty("nameEn").GetString() == "Linked " + suffix), "Public lookup reflects the updated name and stable ID");
                Check(!text.Contains("version", StringComparison.OrdinalIgnoreCase) && !text.Contains("normalized", StringComparison.OrdinalIgnoreCase) && !text.Contains("actorUserId", StringComparison.OrdinalIgnoreCase), "Public DTO exposes no admin metadata");
            }
            web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
            using (var response = await web.PostAsJsonAsync("/api/auth/web/login", new { email = fixture.Admin.Email, password = DoctorReviewFixture.Password })) Check(response.StatusCode == HttpStatusCode.OK, "Admin web login succeeds");
            web.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await web.PostAsJsonAsync(path, new { nameAr = "ويب " + suffix, nameEn = "Web " + suffix })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Cookie-authenticated catalog writes require CSRF");
            web.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
            using (var response = await web.PostAsJsonAsync(path, new { nameAr = "ويب " + suffix, nameEn = "Web " + suffix })) Check(response.StatusCode == HttpStatusCode.Created, "Fresh CSRF permits an Admin web creation");
            using (var response = await anonymous.GetAsync("/openapi/v1.json"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("paths").GetProperty(path + "/{id}").TryGetProperty("delete", out _), "OpenAPI documents the protected delete operation");
            }
            await using var db = fixture.Database.CreateContext();
            var history = await db.SpecialtyCatalogChanges.Where(change => change.SpecialtyId == id).ToArrayAsync();
            Check(history.Length == 3 && history.All(change => change.ActorUserId == fixture.Admin.Id), "SQL retains create/rename/delete history with trusted actor");
            Check(history.Single(change => change.Operation == SpecialtyCatalogOperation.Deleted).PreviousNameEn is not null, "Deletion retains its last display name");
            Check((await db.Specialties.SingleAsync(row => row.Id == fixture.SpecialtyId)).CreatedByUserId is null, "Renaming preserves the original creation actor");
            Check(await db.DoctorRegistrations.AnyAsync(row => row.SpecialtyId == fixture.SpecialtyId), "Doctor registration keeps its specialty relationship");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
            output.WriteLine($"SPECIALTY_ADMIN_HTTP_CHECKS:{checks.Count}; ARTIFACTS:{artifacts}");

            HttpClient Client(bool cookies) => new(new HttpClientHandler { UseCookies = cookies, CookieContainer = new CookieContainer() }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };
            async Task<string> Login(HttpClient client, string email)
            {
                using var response = await client.PostAsJsonAsync("/api/auth/mobile/login", new { email, password = DoctorReviewFixture.Password });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!;
            }
            async Task<string> Version(Guid specialtyId)
            {
                using var response = await admin.GetAsync($"{path}/{specialtyId}"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("version").GetString()!;
            }
            async Task<string> Csrf()
            {
                using var response = await web.GetAsync("/api/auth/web/csrf"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("requestToken").GetString()!;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); lock (log) File.WriteAllText(Path.Combine(artifacts, "api.log"), log.ToString());
        }
    }
    private static bool NoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true;
}
