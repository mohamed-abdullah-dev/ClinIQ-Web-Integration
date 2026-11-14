using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Accounts;

public sealed class DoctorResubmissionConfigurationTests
{
    [Theory]
    [InlineData("garbage", "Development")]
    [InlineData("/relative", "Production")]
    [InlineData("ftp://example.test/form", "Development")]
    [InlineData("https://user:pass@example.test/form", "Development")]
    [InlineData("https://example.test/form?token=old", "Development")]
    [InlineData("https://example.test/form#token=old", "Development")]
    [InlineData("http://example.test/form", "Production")]
    public async Task UnsafeResubmissionDestinationFailsStartupWithoutDatabaseAccess(string url, string environment)
    {
        using var host = Host(url, environment);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
    [Theory]
    [InlineData(null, "Development", "http://localhost:5173/doctor/resubmit")]
    [InlineData(null, "Production", null)]
    [InlineData("https://example.test/form", "Production", "https://example.test/form")]
    public async Task ValidConfigurationStartsWithoutContactingSql(string? url, string environment, string? expected)
    {
        using var host = Host(url, environment); await host.StartAsync();
        Assert.Equal(expected, host.Services.GetRequiredService<IOptions<DoctorResubmissionOptions>>().Value.Url); await host.StopAsync();
    }
    private static IHost Host(string? url, string environment) => new HostBuilder().UseEnvironment(environment)
        .ConfigureAppConfiguration(b => b.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=unused;Integrated Security=True", ["Auth:DoctorResubmissionUrl"] = url,
            ["Auth:PrivateStorageRoot"] = Path.Combine(Integration.DoctorReviewFixture.RepositoryRoot, ".artifacts", "doctor-resubmission", "configuration-private") }))
        .ConfigureServices((ctx, services) =>
        {
            services.AddSingleton<IWebHostEnvironment>(new WebEnvironment(environment));
            services.AddApplication(); services.AddInfrastructure(ctx.Configuration);
        }).Build();
    private sealed class WebEnvironment(string environment) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = Path.Combine(Integration.DoctorReviewFixture.RepositoryRoot, "src", "ClinIQ.API");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
