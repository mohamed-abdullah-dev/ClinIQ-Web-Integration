using System.Text;
using System.Text.Json;
using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PasswordRecoveryTests : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();
    private ServiceProvider? _services;
    private IConfiguration _configuration = null!;
    private readonly string _root = Path.Combine(FindRepository(), ".artifacts", "password-recovery-tests", Guid.NewGuid().ToString("N"));
    private const string OldPassword = "Synthetic-Old1!", NewPassword = "Synthetic-New2!";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return;
        await _database.InitializeAsync();
        await using var context = _database.CreateContext();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = _root,
            ["Auth:PasswordResetUrl"] = "http://localhost:5173/reset-password"
        }).Build();
        _services = BuildServices();
    }
    private ServiceProvider BuildServices(TimeSpan? lifespan = null)
    {
        var environment = new TestEnvironment();
        var services = new ServiceCollection();
        services.AddSingleton(_configuration);
        services.AddSingleton<IHostEnvironment>(environment); services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<TimeProvider>(_database.Clock);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication(); services.AddInfrastructure(_configuration);
        if (lifespan.HasValue) services.Configure<PasswordResetTokenProviderOptions>(o => o.TokenLifespan = lifespan.Value);
        return services.BuildServiceProvider(validateScopes: true);
    }
    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [SqlServerFact]
    public async Task UnknownAndUnconfirmedAccountsGenerateNoResetMessageOrState()
    {
        var user = await User(confirmed: false);
        await Forgot("unknown@example.test"); await Forgot(user.Email!);
        Assert.Equal(0, MailCount());
        await using var context = _database.CreateContext();
        Assert.Empty(await context.UserTokens.ToArrayAsync());
        Assert.False((await context.Users.SingleAsync()).EmailConfirmed);
    }
    [SqlServerFact]
    public async Task ForgotDoesNotChangePasswordSecurityStampLockoutOrExistingSession()
    {
        var user = await User();
        var session = await Login(user, SessionClient.Mobile);
        await using (var scope = _services!.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var fresh = (await users.FindByIdAsync(user.Id.ToString()))!;
            Assert.True((await users.AccessFailedAsync(fresh)).Succeeded);
        }
        await Forgot(user.Email!);
        Assert.Equal(1, MailCount());
        await using var context = _database.CreateContext();
        var saved = await context.Users.SingleAsync();
        Assert.True(saved.PasswordHash == user.PasswordHash && saved.SecurityStamp == user.SecurityStamp);
        Assert.Equal(1, saved.AccessFailedCount);
        Assert.Null(saved.LockoutEnd);
        Assert.NotNull(await Authenticate(session.AccessToken, SessionClient.Mobile));
        var metadata = Assert.Single(await context.UserTokens.ToArrayAsync());
        Assert.Equal("LastEmailAtUtc", metadata.Name);
        Assert.NotNull(metadata.Value);
    }
    [SqlServerFact]
    public async Task CooldownUsesCanonicalUserAndTimeProvider()
    {
        var user = await User();
        await Forgot(user.Email!); await Forgot(user.Email!.ToUpperInvariant());
        Assert.Equal(1, MailCount());
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddMinutes(1);
        await Forgot(user.Email!);
        Assert.Equal(2, MailCount());
    }
    [SqlServerFact]
    public async Task ConcurrentForgotRequestsSendOnlyOneMessage()
    {
        var user = await User();
        await Task.WhenAll(Forgot(user.Email!), Forgot(user.Email!));
        Assert.Equal(1, MailCount());
        await using var context = _database.CreateContext();
        Assert.Single(await context.UserTokens.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task SuccessfulResetRevokesBothClientsChangesStampAndClearsLockout()
    {
        var user = await User();
        var mobile = await Login(user, SessionClient.Mobile);
        var web = await Login(user, SessionClient.Web);
        await using (var scope = _services!.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var fresh = (await users.FindByIdAsync(user.Id.ToString()))!;
            Assert.True((await users.AccessFailedAsync(fresh)).Succeeded);
            Assert.True((await users.SetLockoutEndDateAsync(fresh, DateTimeOffset.MaxValue)).Succeeded);
        }
        await Forgot(user.Email!);
        var proof = await ReadProof();
        Assert.True((await Reset(user.Id, proof, NewPassword)).IsSuccess);
        Assert.True((await Reset(user.Id, proof, NewPassword)).IsError);
        Assert.Null(await Authenticate(mobile.AccessToken, SessionClient.Mobile));
        Assert.Null(await Authenticate(web.AccessToken, SessionClient.Web));
        await using var after = _services!.CreateAsyncScope();
        var sessions = after.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.True((await sessions.RefreshAsync(mobile.RefreshToken, SessionClient.Mobile, default)).IsError);
        var usersAfter = after.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var saved = (await usersAfter.FindByIdAsync(user.Id.ToString()))!;
        Assert.True(saved.SecurityStamp != user.SecurityStamp);
        Assert.False(await usersAfter.CheckPasswordAsync(saved, OldPassword));
        Assert.True(await usersAfter.CheckPasswordAsync(saved, NewPassword));
        Assert.True(saved.EmailConfirmed);
        Assert.Equal(0, saved.AccessFailedCount); Assert.Null(saved.LockoutEnd);
        Assert.Equal(new[] { "Admin","Patient" }, (await usersAfter.GetRolesAsync(saved)).Order(StringComparer.Ordinal).ToArray());
        await using var context = _database.CreateContext();
        Assert.All(await context.AuthSessions.ToArrayAsync(), session => Assert.NotNull(session.RevokedAtUtc));
        Assert.Empty(await context.UserTokens.ToArrayAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "password-reset-notifications"), "*.json"));
    }
    [SqlServerTheory]
    [InlineData("malformed")]
    [InlineData("different-user")]
    [InlineData("confirmation-purpose")]
    public async Task InvalidProofDoesNotChangePassword(string kind)
    {
        var user = await User();
        var proof = await Proof(user);
        if (kind == "malformed") proof = "not-valid-base64!";
        if (kind == "different-user") user = await User();
        if (kind == "confirmation-purpose")
        {
            await using var scope = _services!.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            proof = Encode(await users.GenerateEmailConfirmationTokenAsync((await users.FindByIdAsync(user.Id.ToString()))!));
        }
        Assert.Equal("Account.InvalidPasswordReset", (await Reset(user.Id, proof, NewPassword)).Errors.Single().Code);
        await using var check = _services!.CreateAsyncScope();
        var manager = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(user.Id.ToString()))!, OldPassword));
    }
    [SqlServerFact]
    public async Task UnknownOrUnconfirmedUserCannotResetEvenWithNativeProof()
    {
        var user = await User(confirmed: false);
        var proof = await Proof(user);
        Assert.Equal("Account.InvalidPasswordReset", (await Reset(user.Id, proof, NewPassword)).Errors.Single().Code);
        Assert.Equal("Account.InvalidPasswordReset", (await Reset(Guid.NewGuid(), proof, NewPassword)).Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task WeakPasswordDoesNotConsumeValidProof()
    {
        var user = await User(); var proof = await Proof(user);
        var invalid = await Reset(user.Id, proof, "weak");
        Assert.True(invalid.IsError);
        Assert.All(invalid.Errors, error => Assert.Equal("NewPassword", error.Field));
        Assert.True((await Reset(user.Id, proof, NewPassword)).IsSuccess);
    }
    [SqlServerFact]
    public async Task ConcurrentResetAcceptsOnlyOnePasswordAndProofUse()
    {
        var user = await User(); var proof = await Proof(user);
        var results = await Task.WhenAll(Reset(user.Id, proof, NewPassword), Reset(user.Id, proof, "Synthetic-Other3!"));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("Account.InvalidPasswordReset", Assert.Single(results, result => result.IsError).Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task NamedResetProviderEnforcesExpiryWithoutChangingConfirmationProvider()
    {
        var user = await User(); var proof = await Proof(user);
        await using var expiredServices = BuildServices(TimeSpan.FromTicks(-1));
        await using var scope = expiredServices.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.Equal(PasswordResetTokenProvider.ProviderName, users.Options.Tokens.PasswordResetTokenProvider);
        Assert.Equal(TokenOptions.DefaultProvider, users.Options.Tokens.EmailConfirmationTokenProvider);
        var denied = await scope.ServiceProvider.GetRequiredService<IPasswordRecoveryService>().ResetAsync(user.Id, proof, NewPassword, default);
        Assert.Equal("Account.InvalidPasswordReset", denied.Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task SessionRevocationFailureRollsBackPasswordChange()
    {
        var user = await User(); var proof = await Proof(user);
        var session = await Login(user, SessionClient.Mobile);
        await using (var setup = _database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER [RejectSyntheticRecoveryRevocation] ON [AuthSessions] AFTER UPDATE AS
                BEGIN
                    THROW 51001, 'Synthetic session revocation failure in isolated test.', 1;
                END
                """);
        await Assert.ThrowsAsync<SqlException>(() => Reset(user.Id, proof, NewPassword));
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(user.Id.ToString()))!, OldPassword));
        Assert.NotNull(await Authenticate(session.AccessToken, SessionClient.Mobile));
    }
    [SqlServerFact]
    public async Task EmailFailureDoesNotChangePasswordAndKeepsCooldown()
    {
        var user = await User();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "password-reset-emails"), "Synthetic mail failure fixture.");
        await Forgot(user.Email!);
        await using var context = _database.CreateContext();
        Assert.True((await context.Users.SingleAsync()).SecurityStamp == user.SecurityStamp);
        Assert.Single(await context.UserTokens.ToArrayAsync());
    }
    [SqlServerFact]
    public async Task ResetDoesNotApprovePendingDoctorOrGrantRoles()
    {
        var user = await User(role: null);
        await using (var context = _database.CreateContext())
        {
            var specialty = Specialty.Create("اختبار", "Test").Value; context.Specialties.Add(specialty);
            var documents = new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
                .Select(kind => new DoctorEvidenceMetadata(kind, Guid.NewGuid().ToString("N"), "synthetic.pdf", "application/pdf", 10, new string('A', 64))).ToArray();
            context.DoctorRegistrations.Add(DoctorRegistration.Create(Guid.NewGuid(), user.Id, user.FullName, "01012345678", "SYNTHETIC", specialty.Id, ProfessionalGrade.GeneralPractitioner, documents).Value);
            await context.SaveChangesAsync();
        }
        Assert.True((await Reset(user.Id, await Proof(user), NewPassword)).IsSuccess);
        await using var scope = _services!.CreateAsyncScope();
        var login = await scope.ServiceProvider.GetRequiredService<ISessionService>().LoginAsync(user.Email!, NewPassword, false, SessionClient.Web, default);
        Assert.Equal("Auth.DoctorPendingReview", login.Errors.Single().Code);
        await using var check = _database.CreateContext();
        Assert.Equal(DoctorRegistrationStatus.PendingReview, (await check.DoctorRegistrations.SingleAsync()).Status);
        Assert.Empty(await check.UserRoles.ToArrayAsync());
    }

    private async Task<AppUser> User(bool confirmed = true, string? role = "Admin")
    {
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"recovery-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, FullName = "Synthetic Recovery User", EmailConfirmed = confirmed };
        Assert.True((await users.CreateAsync(user, OldPassword)).Succeeded);
        if (role is not null)
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            if(role=="Admin")
            {
                if(!await roles.RoleExistsAsync("Patient")) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Patient"))).Succeeded);
                Assert.True((await users.AddToRoleAsync(user,"Patient")).Succeeded);
            }
        }
        return user;
    }
    private async Task Forgot(string email)
    {
        await using var scope = _services!.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPasswordRecoveryService>().RequestAsync(email, default);
    }
    private async Task<Result<Success>> Reset(Guid userId, string proof, string password)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPasswordRecoveryService>().ResetAsync(userId, proof, password, default);
    }
    private async Task<string> Proof(AppUser user)
    {
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return Encode(await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(user.Id.ToString()))!));
    }
    private async Task<SessionGrant> Login(AppUser user, SessionClient client)
    {
        await using var scope = _services!.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISessionService>().LoginAsync(user.Email!, OldPassword, false, client, default)).Value;
    }
    private async Task<AuthenticatedSession?> Authenticate(string token, SessionClient client)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionService>().AuthenticateAsync(token, client, default);
    }
    private int MailCount() => Directory.Exists(Path.Combine(_root, "password-reset-emails"))
        ? Directory.GetFiles(Path.Combine(_root, "password-reset-emails"), "*.json").Length : 0;
    private async Task<string> ReadProof()
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(_root, "password-reset-emails"), "*.json").Single()));
        return System.Web.HttpUtility.ParseQueryString(new Uri(json.RootElement.GetProperty("PasswordResetUrl").GetString()!).Query)["token"]!;
    }
    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
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
