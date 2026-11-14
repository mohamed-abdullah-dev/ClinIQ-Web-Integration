using System.Diagnostics;
using System.Text.Json;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class DoctorRegistrationHttpTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [SqlServerFact]
    public async Task RealHttp_RegistersAndConfirmsPendingDoctor_EnforcesValidationCorsAndRateLimits()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        var repository = directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
        var probeRoot = Path.Combine(repository, ".artifacts", "doctor-registration-http", Guid.NewGuid().ToString("N"));
        await using var setup = _database.CreateContext();
        var specialty = Specialty.Create("تخصص HTTP", "HTTP Test Specialty").Value;
        setup.Specialties.Add(specialty);
        await setup.SaveChangesAsync();
        // The fixture keeps its own connections non-pooled for safe teardown. The hosted API uses normal pooling.
        var apiConnection = new SqlConnectionStringBuilder(setup.Database.GetConnectionString())
        {
            Pooling = true, ConnectTimeout = 30, ConnectRetryCount = 0
        }.ConnectionString;
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-File", Path.Combine(repository, "tests/ClinIQ.Infrastructure.Tests/Check-DoctorRegistration.ps1"),
            "-ConnectionString", apiConnection,
            "-SpecialtyId", specialty.Id.ToString(), "-ProbeRoot", probeRoot
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("HTTP probe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var standardOutput = await stdout;
        var standardError = await stderr;
        Assert.True(process.ExitCode == 0, $"HTTP probe failed. {standardError} {standardOutput} Logs: {probeRoot}");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(probeRoot, "report.json")));
        Assert.True(report.RootElement.GetProperty("Passed").GetInt32() >= 20);
        var userId = report.RootElement.GetProperty("UserId").GetGuid();
        await using var reader = _database.CreateContext();
        var user = Assert.Single(await reader.Users.ToArrayAsync());
        Assert.Equal(userId, user.Id);
        Assert.True(user.EmailConfirmed);
        Assert.Empty(await reader.UserRoles.ToArrayAsync());
        var registration = await reader.DoctorRegistrations.Include(row => row.Documents).SingleAsync();
        Assert.Equal(DoctorRegistrationStatus.PendingReview, registration.Status);
        Assert.Null(registration.CreatedByUserId);
        Assert.Equal(4, registration.Documents.Count);
        Assert.All(registration.Documents, document => Assert.Null(document.CreatedByUserId));
        output.WriteLine($"HTTP_PROBE_CHECKS:{report.RootElement.GetProperty("Passed").GetInt32()}");
        output.WriteLine($"HTTP_PROBE_LOGS:{probeRoot}");
        output.WriteLine($"HTTP_TEST_DATABASE:{_database.DatabaseName}");
    }
}
