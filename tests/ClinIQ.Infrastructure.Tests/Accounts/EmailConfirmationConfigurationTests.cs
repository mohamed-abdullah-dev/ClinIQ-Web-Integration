using ClinIQ.Application;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Accounts;

public sealed class EmailConfirmationConfigurationTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/confirm-email")]
    [InlineData("ftp://example.test/confirm-email")]
    [InlineData("https://user:password@example.test/confirm-email")]
    [InlineData("https://example.test/confirm-email?token=old")]
    [InlineData("https://example.test/confirm-email#token")]
    public async Task InvalidConfirmationUrlPreventsHostStartup(string url)
    {
        using var host = CreateHost(url);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData(null, "http://localhost:5173/confirm-email")]
    [InlineData("http://localhost:5173/confirm-email", "http://localhost:5173/confirm-email")]
    [InlineData("https://example.test/confirm-email", "https://example.test/confirm-email")]
    public async Task ValidConfigurationStartsWithoutDatabaseAccess(string? configured, string expected)
    {
        using var host = CreateHost(configured);
        await host.StartAsync();
        Assert.Equal(expected, host.Services.GetRequiredService<IOptions<EmailConfirmationOptions>>().Value.Url);
        await host.StopAsync();
    }

    [Fact]
    public async Task ProviderFailureIsReportedAsUnavailableWithoutDatabaseAccess()
    {
        using var host = CreateHost(null);
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        users.RegisterTokenProvider(users.Options.Tokens.EmailConfirmationTokenProvider, new FailingProvider());
        var email = scope.ServiceProvider.GetRequiredService<AccountEmailConfirmationService>();
        var result = await email.SendAsync(new AppUser { Id = Guid.NewGuid(), Email = "synthetic@example.test" }, default);
        Assert.Equal("Unavailable", result);
    }

    private static IHost CreateHost(string? url) => new HostBuilder()
        .UseEnvironment(Environments.Development)
        .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // No test database is created or contacted by these configuration tests.
            ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=unused;Integrated Security=True",
            ["Auth:EmailConfirmationUrl"] = url
        }))
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<IWebHostEnvironment>(new TestWebEnvironment());
            services.AddApplication();
            services.AddInfrastructure(context.Configuration);
        })
        .Build();

    private sealed class TestWebEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FailingProvider : IUserTwoFactorTokenProvider<AppUser>
    {
        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);
        public Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user) =>
            throw new InvalidOperationException("Synthetic delivery preparation failure.");
        public Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);
    }
}
