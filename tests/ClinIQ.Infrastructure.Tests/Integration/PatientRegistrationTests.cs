using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterPatient;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using MediatR;
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
public sealed class PatientRegistrationTests : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();
    private ServiceProvider? _services;
    private readonly string _root = Path.Combine(FindRepository(), ".artifacts", "patient-registration-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return;
        await _database.InitializeAsync();
        await using var context = _database.CreateContext();
        var environment = new TestEnvironment();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = _root
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<TimeProvider>(_database.Clock);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication(); services.AddInfrastructure(config);
        _services = services.BuildServiceProvider(validateScopes: true);
    }
    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [SqlServerFact]
    public async Task RegistersUnconfirmedAccountWithPatientRoleAndNoSession()
    {
        var receipt = (await Register(Command())).Value;
        Assert.True(receipt.EmailConfirmationRequired);
        Assert.Equal("DevelopmentFile", receipt.EmailDeliveryStatus);
        await using var context = _database.CreateContext();
        var user = await context.Users.SingleAsync();
        Assert.Equal(receipt.UserId, user.Id);
        Assert.Equal("Synthetic Patient", user.FullName);
        Assert.False(user.EmailConfirmed);
        Assert.Null(user.PhoneNumber);
        Assert.True(!string.IsNullOrWhiteSpace(user.PasswordHash) && user.PasswordHash != Command().Password);
        Assert.Equal("Patient", (await context.Roles.SingleAsync()).Name);
        Assert.Equal(user.Id, (await context.UserRoles.SingleAsync()).UserId);
        Assert.Empty(await context.AuthSessions.ToArrayAsync());
        Assert.Empty(await context.DoctorRegistrations.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task DuplicateEmailDoesNotOverwriteAccount()
    {
        var first = (await Register(Command())).Value;
        var second = await Register(Command() with { Email = "PATIENT@EXAMPLE.TEST", FullName = "Changed Name" });
        Assert.Equal("Account.EmailTaken", second.Errors.Single().Code);
        await using var context = _database.CreateContext();
        var user = await context.Users.SingleAsync();
        Assert.Equal(first.UserId, user.Id);
        Assert.Equal("Synthetic Patient", user.FullName);
        Assert.Single(await context.UserRoles.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task ConcurrentSameEmailCreatesExactlyOneAccount()
    {
        var results = await Task.WhenAll(Register(Command()), Register(Command() with { Email = "PATIENT@EXAMPLE.TEST" }));
        Assert.Single(results, x => x.IsSuccess);
        var rejected = Assert.Single(results, x => x.IsError);
        Assert.All(rejected.Errors, error => Assert.Equal("Account.EmailTaken", error.Code));
        await using var context = _database.CreateContext();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(1, await context.Roles.CountAsync());
        Assert.Equal(1, await context.UserRoles.CountAsync());
    }
    [SqlServerFact]
    public async Task ConcurrentDifferentEmailsProvisionPatientRoleOnlyOnce()
    {
        var results = await Task.WhenAll(Register(Command()), Register(Command() with { Email = "other@example.test" }));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        await using var context = _database.CreateContext();
        Assert.Equal(2, await context.Users.CountAsync());
        Assert.Equal(1, await context.Roles.CountAsync());
        Assert.Equal(2, await context.UserRoles.CountAsync());
    }
    [SqlServerFact]
    public async Task WeakPasswordWritesNeitherUserNorRole()
    {
        var result = await Register(Command() with { Password = "weak" });
        Assert.True(result.IsError);
        Assert.All(result.Errors, error => Assert.Equal("Password", error.Field));
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Empty(await context.Roles.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task RoleAssignmentDatabaseFailureRollsBackWholeRegistration()
    {
        await using (var setup = _database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER [RejectSyntheticPatientRole] ON [AspNetUserRoles] AFTER INSERT AS
                BEGIN
                    THROW 51000, 'Synthetic role assignment failure for isolated test.', 1;
                END
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Register(Command()));
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Empty(await context.Roles.ToArrayAsync());
        Assert.Empty(await context.UserRoles.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task EmailWriteFailureKeepsCommittedAccountAndReportsUnavailable()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "development-emails"), "Synthetic delivery failure fixture.");
        var receipt = (await Register(Command())).Value;
        Assert.Equal("Unavailable", receipt.EmailDeliveryStatus);
        await using var context = _database.CreateContext();
        Assert.Equal(receipt.UserId, (await context.Users.SingleAsync()).Id);
        Assert.Single(await context.UserRoles.ToArrayAsync());
        Assert.Empty(await context.AuthSessions.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task InvalidConfirmationConfigurationWritesNoAccount()
    {
        _services!.GetRequiredService<IConfiguration>()["Auth:EmailConfirmationUrl"] = "invalid-url";
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => Register(Command()));
        await using var context = _database.CreateContext();
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Empty(await context.Roles.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task TokenGenerationFailureReportsCommittedAccountInsteadOfFailingRegistration()
    {
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        users.RegisterTokenProvider(users.Options.Tokens.EmailConfirmationTokenProvider, new FailingConfirmationProvider());
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(Command());
        Assert.True(result.IsSuccess);
        Assert.Equal("Unavailable", result.Value.EmailDeliveryStatus);
        await using var context = _database.CreateContext();
        Assert.Equal(result.Value.UserId, (await context.Users.SingleAsync()).Id);
        Assert.False((await context.Users.SingleAsync()).EmailConfirmed);
        Assert.Single(await context.UserRoles.ToArrayAsync());
        Assert.Empty(await context.AuthSessions.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task ConfirmationEnablesMobilePatientLoginButNotWebLogin()
    {
        var receipt = (await Register(Command())).Value;
        await using (var before = _services!.CreateAsyncScope())
        {
            var denied = await before.ServiceProvider.GetRequiredService<ISessionService>()
                .LoginAsync(Command().Email, Command().Password, false, ClinIQ.Domain.Accounts.SessionClient.Mobile, default);
            Assert.Equal("Auth.EmailUnconfirmed", denied.Errors.Single().Code);
        }
        await using (var scope = _services!.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(receipt.UserId.ToString()))!;
            var token = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
            Assert.True((await scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>().ConfirmAsync(user.Id, token, default)).IsSuccess);
        }
        await using var after = _services!.CreateAsyncScope();
        var sessions = after.ServiceProvider.GetRequiredService<ISessionService>();
        var mobile = await sessions.LoginAsync(Command().Email, Command().Password, false, ClinIQ.Domain.Accounts.SessionClient.Mobile, default);
        Assert.Equal(new[] { "Patient" }, mobile.Value.User.Roles);
        var web = await sessions.LoginAsync(Command().Email, Command().Password, false, ClinIQ.Domain.Accounts.SessionClient.Web, default);
        Assert.Equal("Auth.ClientNotAllowed", web.Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task ReusesExistingPatientRoleForLaterRegistrations()
    {
        Assert.True((await Register(Command())).IsSuccess);
        Assert.True((await Register(Command() with { Email = "next@example.test" })).IsSuccess);
        await using var context = _database.CreateContext();
        Assert.Equal(1, await context.Roles.CountAsync());
        Assert.Equal(2, await context.UserRoles.CountAsync());
    }
    private async Task<Result<PatientRegistrationReceipt>> Register(RegisterPatientCommand command)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }
    private static RegisterPatientCommand Command() => new("Synthetic Patient", "patient@example.test", "Synthetic-Test1!");
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private sealed class FailingConfirmationProvider : IUserTwoFactorTokenProvider<AppUser>
    {
        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);
        public Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user) =>
            throw new InvalidOperationException("Synthetic confirmation-provider failure.");
        public Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);
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
