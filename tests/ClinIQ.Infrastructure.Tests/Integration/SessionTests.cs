using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class SessionTests(SqlServerIntegrationFixture database) : IClassFixture<SqlServerIntegrationFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;
    private readonly LoginReadBarrier _loginReads = new();
    private const string Password = "Synthetic-Test1!";

    public Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return Task.CompletedTask;
        using var context = database.CreateContext();
        var environment = new TestEnvironment();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../../.artifacts/sessions", Guid.NewGuid().ToString("N")))
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<TimeProvider>(database.Clock);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(_loginReads));
        _services = services.BuildServiceProvider(validateScopes: true);
        database.Clock.UtcNow = SqlServerIntegrationFixture.InitialUtc;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() { if (_services is not null) await _services.DisposeAsync(); }

    [SqlServerFact]
    public async Task PendingDoctorGetsMessageOnlyAfterCorrectPassword()
    {
        var user = await User([], pending: true);
        Assert.Equal("Auth.InvalidCredentials", (await Login(user, "Wrong1!")).Errors.Single().Code);
        Assert.Equal("Auth.DoctorPendingReview", (await Login(user)).Errors.Single().Code);
        await using var db = database.CreateContext();
        Assert.Empty(await db.AuthSessions.Where(x => x.UserId == user.Id).ToArrayAsync());
    }
    [SqlServerFact]
    public async Task UnconfirmedUserCannotLogin()
    {
        var user = await User(["Admin"], confirmed: false);
        Assert.Equal("Auth.EmailUnconfirmed", (await Login(user)).Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task ClientCannotGrantItselfAnUnapprovedRole()
    {
        var user = await User(["Patient"]);
        Assert.Equal("Auth.ClientNotAllowed", (await Login(user)).Errors.Single().Code);
        Assert.True((await Login(user, client: SessionClient.Mobile)).IsSuccess);
    }
    [SqlServerFact]
    public async Task PendingDoctorDoesNotDisableIndependentAdmin()
    {
        var user = await User(["Admin", "Doctor"], pending: true);
        var grant = (await Login(user)).Value;
        Assert.Equal(new[] { "Admin" }, grant.User.Roles);
    }
    [SqlServerFact]
    public async Task ApprovedDoctorCanLoginOnlyOnWeb()
    {
        var user = await User(["Doctor"], pending: true);
        await using (var db = database.CreateContext())
            await db.DoctorRegistrations.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, DoctorRegistrationStatus.Approved));
        Assert.Equal(new[] { "Doctor" }, (await Login(user)).Value.User.Roles);
        Assert.Equal("Auth.ClientNotAllowed", (await Login(user, client: SessionClient.Mobile)).Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task RefreshRotatesSecretsAndRejectsOldAccess()
    {
        var first = (await Login(await User(["Admin"]))).Value;
        var next = (await Refresh(first.RefreshToken)).Value;
        Assert.NotEqual(first.AccessToken, next.AccessToken);
        Assert.NotEqual(first.RefreshToken, next.RefreshToken);
        Assert.Equal(first.ExpiresAtUtc, next.ExpiresAtUtc);
        Assert.Null(await Authenticate(first.AccessToken));
        Assert.NotNull(await Authenticate(next.AccessToken));
        await using var db = database.CreateContext();
        var row = await db.AuthSessions.SingleAsync(x => x.Id == first.SessionId);
        Assert.DoesNotContain(next.AccessToken, row.AccessHash);
        Assert.Equal(64, row.AccessHash.Length);
    }
    [SqlServerFact]
    public async Task ReplayedRefreshRevokesFamilyButWrongSecretDoesNot()
    {
        var first = (await Login(await User(["Admin"]))).Value;
        var forged = first.RefreshToken[..33] + SessionSecrets.Create();
        Assert.True((await Refresh(forged)).IsError);
        Assert.NotNull(await Authenticate(first.AccessToken));
        var next = (await Refresh(first.RefreshToken)).Value;
        Assert.True((await Refresh(first.RefreshToken)).IsError);
        Assert.Null(await Authenticate(next.AccessToken));
        Assert.True((await Refresh(next.RefreshToken)).IsError);
    }
    [SqlServerFact]
    public async Task ConcurrentRefreshHasOneWinnerAndReplayRevokesSession()
    {
        var first = (await Login(await User(["Admin"]))).Value;
        var results = await Task.WhenAll(Refresh(first.RefreshToken), Refresh(first.RefreshToken));
        Assert.Single(results, x => x.IsSuccess);
        Assert.Single(results, x => x.IsError);
        Assert.Null(await Authenticate(results.Single(x => x.IsSuccess).Value.AccessToken));
    }
    [SqlServerFact]
    public async Task LogoutRevokesAccessAndRefreshAndIsIdempotent()
    {
        var grant = (await Login(await User(["Admin"]))).Value;
        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.True((await service.LogoutAsync(grant.RefreshToken, SessionClient.Web, default)).IsSuccess);
        Assert.True((await service.LogoutAsync(grant.RefreshToken, SessionClient.Web, default)).IsSuccess);
        Assert.Null(await Authenticate(grant.AccessToken));
        Assert.True((await Refresh(grant.RefreshToken)).IsError);
    }
    [SqlServerFact]
    public async Task ExpiredAccessCanRefreshButAbsoluteExpiryCannot()
    {
        var first = (await Login(await User(["Admin"]))).Value;
        database.Clock.UtcNow = first.AccessExpiresAtUtc;
        Assert.Null(await Authenticate(first.AccessToken));
        var next = (await Refresh(first.RefreshToken)).Value;
        database.Clock.UtcNow = next.ExpiresAtUtc;
        Assert.Null(await Authenticate(next.AccessToken));
        Assert.True((await Refresh(next.RefreshToken)).IsError);
    }
    [SqlServerFact]
    public async Task SecurityStampChangeImmediatelyInvalidatesSession()
    {
        var user = await User(["Admin"]);
        var grant = (await Login(user)).Value;
        await using var scope = _services!.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True((await manager.UpdateSecurityStampAsync((await manager.FindByIdAsync(user.Id.ToString()))!)).Succeeded);
        Assert.Null(await Authenticate(grant.AccessToken));
        Assert.True((await Refresh(grant.RefreshToken)).IsError);
    }
    [SqlServerFact]
    public async Task RoleRemovalImmediatelyRemovesAccess()
    {
        var user = await User(["Admin"]);
        var grant = (await Login(user)).Value;
        await using var scope = _services!.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True((await manager.RemoveFromRoleAsync((await manager.FindByIdAsync(user.Id.ToString()))!, "Admin")).Succeeded);
        Assert.Null(await Authenticate(grant.AccessToken));
    }
    [SqlServerFact]
    public async Task FiveWrongPasswordsTemporarilyBlockLogin()
    {
        var user = await User(["Admin"]);
        for (var i = 0; i < 5; i++) Assert.True((await Login(user, "Wrong1!")).IsError);
        Assert.Equal("Auth.InvalidCredentials", (await Login(user)).Errors.Single().Code);
    }
    [SqlServerFact]
    public async Task ConcurrentWrongPasswordsAreAllCountedBeforeAnotherLogin()
    {
        var user = await User(["Admin"]);
        // Force five separate scopes to read the same initial account snapshot.
        _loginReads.Arm(5);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Login(user, "Wrong1!")));
        Assert.All(results, result => Assert.Equal("Auth.InvalidCredentials", result.Errors.Single().Code));
        await using var scope = _services!.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await manager.IsLockedOutAsync((await manager.FindByIdAsync(user.Id.ToString()))!));
        Assert.Equal("Auth.InvalidCredentials", (await Login(user)).Errors.Single().Code);
        await using var context = database.CreateContext();
        Assert.Empty(await context.AuthSessions.Where(x => x.UserId == user.Id).ToArrayAsync());
    }
    [SqlServerFact]
    public async Task RefreshCannotSwitchClient()
    {
        var grant = (await Login(await User(["Admin"])) ).Value;
        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.Null(await service.AuthenticateAsync(grant.AccessToken, SessionClient.Mobile, default));
        Assert.True((await service.RefreshAsync(grant.RefreshToken, SessionClient.Mobile, default)).IsError);
        Assert.NotNull(await Authenticate(grant.AccessToken));
    }

    private async Task<AppUser> User(string[] roles, bool confirmed = true, bool pending = false)
    {
        await using var scope = _services!.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var email = $"session-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, FullName = "Synthetic session user", EmailConfirmed = confirmed };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role)) Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
            Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        }
        if (pending)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var specialty = Specialty.Create($"اختبار {Guid.NewGuid():N}", $"Test {Guid.NewGuid():N}").Value;
            db.Specialties.Add(specialty);
            var documents = new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
                .Select(kind => new DoctorEvidenceMetadata(kind, Guid.NewGuid().ToString("N"), "synthetic.pdf", "application/pdf", 10, new string('A', 64))).ToArray();
            db.DoctorRegistrations.Add(DoctorRegistration.Create(Guid.NewGuid(), user.Id, user.FullName, "01012345678", "TEST", specialty.Id, ProfessionalGrade.GeneralPractitioner, documents).Value);
            await db.SaveChangesAsync();
        }
        return user;
    }
    private async Task<ClinIQ.Domain.Common.Results.Result<SessionGrant>> Login(AppUser user, string password = Password, SessionClient client = SessionClient.Web)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionService>().LoginAsync(user.Email!, password, false, client, default);
    }
    private async Task<ClinIQ.Domain.Common.Results.Result<SessionGrant>> Refresh(string token)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionService>().RefreshAsync(token, SessionClient.Web, default);
    }
    private async Task<AuthenticatedSession?> Authenticate(string token)
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionService>().AuthenticateAsync(token, SessionClient.Web, default);
    }
    private sealed class LoginReadBarrier : DbCommandInterceptor
    {
        private int _remaining;
        private TaskCompletionSource? _ready;

        public void Arm(int count)
        {
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _remaining = count;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (_ready is not null && command.CommandText.Contains("].[NormalizedEmail] =", StringComparison.Ordinal))
            {
                var left = Interlocked.Decrement(ref _remaining);
                if (left == 0) _ready.TrySetResult();
                if (left >= 0) await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ClinIQ.API"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
