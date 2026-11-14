using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClinIQ.Application.Features.Subscriptions;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorSubscriptionHttpTests(DoctorReviewFixture fixture) : IClassFixture<DoctorReviewFixture>
{
    [SqlServerFact]
    public async Task DoctorWebCanActivateWithCsrfAndAdminCannotActivate()
    {
        var approved = await PracticeLocationTestData.ApprovedDoctor(fixture);
        var root = Path.Combine(DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", "Release", "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.Connection;
        start.Environment["Auth__PrivateStorageRoot"] = fixture.PrivateRoot;
        start.Environment["FeedbackNotifications__WorkerEnabled"] = "false";
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            lock (log) log.AppendLine(e.Data);
            const string marker = "Now listening on: ";
            var index = e.Data?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (index >= 0) listening.TrySetResult(new Uri(e.Data![(index + marker.Length)..].Trim()));
        };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(45));
            using var guest = Client(address);
            using var doctor = Client(address);
            using var admin = Client(address);
            const string statusPath = "/api/doctors/me/subscription";
            const string activationPath = statusPath + "/pro-trial";

            using (var response = await guest.GetAsync(statusPath)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await HttpSessionTestSupport.WebLoginAsync(admin, fixture.Admin.Email!);
            using (var response = await admin.PostAsync(activationPath, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await HttpSessionTestSupport.WebLoginAsync(doctor, approved.Email);
            using (var response = await doctor.GetAsync(statusPath))
            {
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("Standard", (await response.Content.ReadFromJsonAsync<DoctorSubscriptionDto>())!.EffectivePlan);
            }
            doctor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var response = await doctor.PostAsync(activationPath, null)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null((await doctor.GetFromJsonAsync<DoctorSubscriptionDto>(statusPath))!.Trial);

            doctor.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await HttpSessionTestSupport.CsrfAsync(doctor));
            using var activated = await doctor.PostAsync(activationPath, null);
            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
            var first = await activated.Content.ReadFromJsonAsync<DoctorSubscriptionDto>();
            Assert.Equal("Pro", first!.EffectivePlan);
            using var retry = await doctor.PostAsync(activationPath, null);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(first, await retry.Content.ReadFromJsonAsync<DoctorSubscriptionDto>());
            Assert.Contains(activationPath, await guest.GetStringAsync("/openapi/v1.json"), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            var artifact = Path.Combine(DoctorReviewFixture.RepositoryRoot, ".artifacts", "p12-subscriptions", "http-api.log");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            await File.WriteAllTextAsync(artifact, log.ToString());
        }
    }

    private static HttpClient Client(Uri address) => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
    {
        BaseAddress = address,
        Timeout = TimeSpan.FromSeconds(30)
    };
}
