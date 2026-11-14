using System.Text.Json;
using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class FirstAdminBootstrapTests : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();
    private readonly TestEnvironment _environment = new();
    private ServiceProvider? _services;
    private readonly string _root = Path.Combine(FindRepository(), ".artifacts", "admin-bootstrap-tests", Guid.NewGuid().ToString("N"));
    private const string Password = "Synthetic-Admin1!";
    private static AdminBootstrapInput Input(string email = "admin@example.test", string password = Password) =>
        new(" Synthetic Admin ", email, password);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return;
        await _database.InitializeAsync();
        await using var context = _database.CreateContext();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = _root,
            ["Auth:EmailConfirmationUrl"] = "http://localhost:5173/confirm-email"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(_environment); services.AddSingleton<IWebHostEnvironment>(_environment);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication(); services.AddInfrastructure(configuration);
        _services = services.BuildServiceProvider(validateScopes: true);
    }
    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [SqlServerFact]
    public async Task CreatesOnlyAdminAndRequiresConfirmationBeforeBothClientLogins()
    {
        var result = await Bootstrap(Input(" admin@example.test "));
        Assert.True(result.IsSuccess);
        Assert.Equal("DevelopmentFile", result.Value.EmailDeliveryStatus);
        await using (var context = _database.CreateContext())
        {
            var user = await context.Users.SingleAsync();
            Assert.Equal(result.Value.UserId, user.Id);
            Assert.Equal("Synthetic Admin", user.FullName); Assert.Equal("admin@example.test", user.Email);
            Assert.False(user.EmailConfirmed); Assert.NotNull(user.PasswordHash);
            Assert.Equal("Admin", (await context.Roles.SingleAsync()).Name);
            Assert.Single(await context.UserRoles.ToArrayAsync());
            Assert.Empty(await context.AuthSessions.ToArrayAsync());
            Assert.Empty(await context.DoctorRegistrations.ToArrayAsync());
        }
        foreach (var client in new[] { SessionClient.Web, SessionClient.Mobile })
        {
            await using var scope = _services!.CreateAsyncScope();
            var login = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .LoginAsync("admin@example.test", Password, false, client, default);
            Assert.Equal("Auth.EmailUnconfirmed", Assert.Single(login.Errors).Code);
        }
        var mail = Directory.GetFiles(Path.Combine(_root, "development-emails"), "*.json").Single();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(mail));
        var query = QueryHelpers.ParseQuery(new Uri(json.RootElement.GetProperty("ConfirmationUrl").GetString()!).Query);
        await using (var scope = _services!.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>()
                .ConfirmAsync(result.Value.UserId, query["token"].ToString(), default)).IsSuccess);
        foreach (var client in new[] { SessionClient.Web, SessionClient.Mobile })
        {
            await using var scope = _services!.CreateAsyncScope();
            var login = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .LoginAsync("admin@example.test", Password, false, client, default);
            if(client==SessionClient.Web){Assert.True(login.IsSuccess);Assert.Equal(new[]{"Admin"},login.Value.User.Roles);}
            else Assert.Equal(SessionErrors.ClientNotAllowed,Assert.Single(login.Errors));
        }
    }

    [SqlServerFact]
    public async Task RepeatDoesNotOverwritePasswordOrCreateAnotherAdmin()
    {
        var first = await Bootstrap(Input()); Assert.True(first.IsSuccess);
        var repeat = await Bootstrap(Input(password: "Synthetic-Different2!"));
        var differentEmail = await Bootstrap(Input("second@example.test"));
        Assert.Equal(AdminBootstrapErrors.AlreadyInitialized, Assert.Single(repeat.Errors));
        Assert.Equal(AdminBootstrapErrors.AlreadyInitialized, Assert.Single(differentEmail.Errors));
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(first.Value.UserId.ToString()))!, Password));
        await using var context = _database.CreateContext();
        Assert.Single(await context.Users.ToArrayAsync()); Assert.Single(await context.Roles.ToArrayAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "development-emails"), "*.json"));
    }

    [SqlServerFact]
    public async Task ExistingPatientCannotBePromotedAndKeepsCredentials()
    {
        await using (var scope = _services!.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IPatientRegistrationService>()
                .RegisterAsync(new("Patient", "admin@example.test", Password), default);
            Assert.True(result.IsSuccess);
        }
        var rejected = await Bootstrap(Input("ADMIN@example.test", "Synthetic-Different2!"));
        Assert.Equal(AccountErrors.EmailTaken, Assert.Single(rejected.Errors));
        await using var context = _database.CreateContext();
        Assert.Equal("Patient", (await context.Roles.SingleAsync()).Name);
        Assert.Equal("Patient", (await context.Users.SingleAsync()).FullName);
        Assert.Single(await context.UserRoles.ToArrayAsync());
        await using var check = _services!.CreateAsyncScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByEmailAsync("admin@example.test"))!, Password));
    }

    [SqlServerFact]
    public async Task ConcurrentAttemptsHaveOneWinner()
    {
        var results = await Task.WhenAll(Bootstrap(Input()), Bootstrap(Input("other@example.test")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(AdminBootstrapErrors.AlreadyInitialized, Assert.Single(results.Single(result => result.IsError).Errors));
        await using var context = _database.CreateContext();
        Assert.Single(await context.Users.ToArrayAsync()); Assert.Single(await context.Roles.ToArrayAsync());
        Assert.Single(await context.UserRoles.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task RoleAssignmentFailureRollsBackUserAndRole()
    {
        await using (var context = _database.CreateContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailSyntheticAdminAssignment ON AspNetUserRoles AFTER INSERT AS
                THROW 51002, 'Synthetic admin assignment failure.', 1;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await Bootstrap(Input()));
        await using var reader = _database.CreateContext();
        Assert.Empty(await reader.Users.ToArrayAsync()); Assert.Empty(await reader.Roles.ToArrayAsync());
        Assert.Empty(await reader.UserRoles.ToArrayAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "development-emails")));
    }

    [SqlServerFact]
    public async Task EmptyExistingAdminRoleStillClosesBootstrap()
    {
        var first = await Bootstrap(Input()); Assert.True(first.IsSuccess);
        await using (var scope = _services!.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByIdAsync(first.Value.UserId.ToString()))!)).Succeeded);
        }
        Assert.Equal(AdminBootstrapErrors.AlreadyInitialized, Assert.Single((await Bootstrap(Input())).Errors));
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync()); Assert.Single(await context.Roles.ToArrayAsync());
    }

    [SqlServerTheory]
    [InlineData("weak")]
    [InlineData("")]
    public async Task InvalidPasswordLeavesNoPartialAdmin(string password)
    {
        Assert.True((await Bootstrap(Input(password: password))).IsError);
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync()); Assert.Empty(await context.Roles.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task ProductionServiceCannotBootstrap()
    {
        _environment.EnvironmentName = Environments.Production;
        Assert.Equal(AdminBootstrapErrors.DevelopmentOnly, Assert.Single((await Bootstrap(Input())).Errors));
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync()); Assert.Empty(await context.Roles.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task DeliveryFailureReportsCommittedUnconfirmedAccount()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "development-emails"), "Synthetic blocked mail folder.");
        var result = await Bootstrap(Input());
        Assert.True(result.IsSuccess); Assert.Equal("Unavailable", result.Value.EmailDeliveryStatus);
        await using var context = _database.CreateContext();
        Assert.False((await context.Users.SingleAsync()).EmailConfirmed);
        Assert.Single(await context.UserRoles.ToArrayAsync());
        Assert.Equal(AdminBootstrapErrors.AlreadyInitialized, Assert.Single((await Bootstrap(Input())).Errors));
    }

    [SqlServerFact]
    public async Task PendingMigrationIsReportedWithoutApplyingIt()
    {
        await using (var context = _database.CreateContext())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId = N'20261025211857_AuthSessions'");
        Assert.Equal(AdminBootstrapErrors.MigrationsPending, Assert.Single((await Bootstrap(Input())).Errors));
        await using var reader = _database.CreateContext();
        Assert.Empty(await reader.Users.ToArrayAsync()); Assert.Empty(await reader.Roles.ToArrayAsync());
        Assert.DoesNotContain("20261025211857_AuthSessions", await reader.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20261025211857_AuthSessions", await reader.Database.GetPendingMigrationsAsync());
    }

    private async Task<Result<AdminBootstrapReceipt>> Bootstrap(AdminBootstrapInput input)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FirstAdminBootstrapper>().BootstrapAsync(input);
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = Path.Combine(FindRepository(), "src", "ClinIQ.API");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
