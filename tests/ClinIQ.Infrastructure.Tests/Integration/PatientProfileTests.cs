using ClinIQ.Application;
using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Application.Features.Patients;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

[Trait("Category", "SqlServerIntegration")]
public sealed class PatientProfileTests(SqlServerIntegrationFixture database) : IClassFixture<SqlServerIntegrationFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;

    public Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return Task.CompletedTask;
        using var context = database.CreateContext();
        var environment = new TestEnvironment();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = Path.Combine(Path.GetTempPath(), "ClinIQ-profile-tests", Guid.NewGuid().ToString("N"))
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<TimeProvider>(database.Clock);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication(); services.AddInfrastructure(config);
        services.AddScoped<Actor>(); services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<Actor>());
        _services = services.BuildServiceProvider(validateScopes: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() { if (_services is not null) await _services.DisposeAsync(); }

    [SqlServerFact]
    public async Task OwnProfileCanBeCreatedReadAndUpdatedWithTrustedAuditAndStableIdentity()
    {
        var user = await User();
        Assert.Equal("PatientProfile.NotFound", (await Get(user.Id)).Errors.Single().Code);
        var first = (await Save(user.Id)).Value;
        Assert.True(first.Created);
        Assert.Equal(user.Id, first.Profile.UserId);
        Assert.Equal(first.Profile, (await Get(user.Id)).Value);
        var next = (await Save(user.Id, first.Profile.Version, "Updated patient")).Value;
        Assert.False(next.Created);
        Assert.Equal(first.Profile.Id, next.Profile.Id);
        Assert.NotEqual(first.Profile.Version, next.Profile.Version);
        await using var context = database.CreateContext();
        var stored = await context.PatientProfiles.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(user.Id, stored.CreatedByUserId);
        Assert.Equal(user.Id, stored.LastModifiedByUserId);
        Assert.Equal(user.FullName, (await context.Users.SingleAsync(x => x.Id == user.Id)).FullName);
    }

    [SqlServerFact]
    public async Task DifferentUsersKeepTheirOwnProfilesEvenWithTheSamePhone()
    {
        var first = await User(); var second = await User();
        var firstProfile = (await Save(first.Id, name: "First")).Value.Profile;
        var secondProfile = (await Save(second.Id, name: "Second")).Value.Profile;
        Assert.NotEqual(firstProfile.Id, secondProfile.Id);
        Assert.Equal(firstProfile, (await Get(first.Id)).Value);
        Assert.Equal(secondProfile, (await Get(second.Id)).Value);
    }

    [SqlServerFact]
    public async Task ConcurrentCreationHasOneWinnerAndNoDuplicatePatient()
    {
        var user = await User();
        var results = await Task.WhenAll(Save(user.Id), Save(user.Id));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("PatientProfile.StaleVersion", results.Single(result => result.IsError).Errors.Single().Code);
        await using var context = database.CreateContext();
        Assert.Equal(1, await context.PatientProfiles.CountAsync(x => x.UserId == user.Id));
    }

    [SqlServerFact]
    public async Task ConcurrentUpdatesDoNotSilentlyOverwriteContact()
    {
        var user = await User(); var first = (await Save(user.Id)).Value.Profile;
        var results = await Task.WhenAll(Save(user.Id, first.Version, "First update"), Save(user.Id, first.Version, "Second update"));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("PatientProfile.StaleVersion", results.Single(result => result.IsError).Errors.Single().Code);
        Assert.Equal(results.Single(result => result.IsSuccess).Value.Profile, (await Get(user.Id)).Value);
    }

    [SqlServerTheory]
    [InlineData(null, true)]
    [InlineData("Admin", true)]
    [InlineData("Doctor", true)]
    [InlineData("Patient", false)]
    public async Task ConfirmedPatientRoleIsRequiredInTheService(string? role, bool confirmed)
    {
        var user = await User(role, confirmed);
        Assert.Equal("PatientProfile.Forbidden", (await Save(user.Id)).Errors.Single().Code);
        Assert.Equal("PatientProfile.Forbidden", (await Get(user.Id)).Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task AnonymousServiceCallsAreUnauthorized()
    {
        Assert.Equal("PatientProfile.Unauthorized", (await Save(null)).Errors.Single().Code);
        Assert.Equal("PatientProfile.Unauthorized", (await Get(null)).Errors.Single().Code);
    }

    [SqlServerFact]
    public async Task InvalidContactAndMissingVersionLeaveExistingDataUnchanged()
    {
        var user = await User(); var first = (await Save(user.Id)).Value.Profile;
        Assert.Equal("PatientProfile.StaleVersion", (await Save(user.Id)).Errors.Single().Code);
        Assert.True((await Save(user.Id, first.Version, "New name", "bad-phone")).IsError);
        Assert.Equal(first, (await Get(user.Id)).Value);
    }

    private async Task<Result<PatientProfileSaveResult>> Save(Guid? owner, string? version = null, string name = "Synthetic patient", string phone = "01012345678")
    {
        await using var scope = _services!.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<Actor>().UserId = owner;
        return await scope.ServiceProvider.GetRequiredService<IPatientProfileService>().SaveOwnAsync(name, phone, version, default);
    }
    private async Task<Result<PatientProfileDto>> Get(Guid? owner)
    {
        await using var scope = _services!.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<Actor>().UserId = owner;
        return await scope.ServiceProvider.GetRequiredService<IPatientProfileService>().GetOwnAsync(default);
    }
    private async Task<AppUser> User(string? role = "Patient", bool confirmed = true)
    {
        await using var scope = _services!.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var email = $"patient-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, FullName = "Synthetic account", EmailConfirmed = confirmed };
        Assert.True((await users.CreateAsync(user, "Synthetic-Patient1!")).Succeeded);
        if (role is not null)
        {
            if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }
        return user;
    }
    private sealed class Actor : ICurrentUser { public Guid? UserId { get; set; } }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
