using System.Text;
using System.Text.Json;
using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterDoctor;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Accounts;
using ClinIQ.Infrastructure.Data;
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
public sealed class DoctorRegistrationTests : IAsyncLifetime
{
    private readonly SqlServerIntegrationFixture _database = new();
    private ServiceProvider? _services;
    private readonly string _root = Path.Combine(FindRepositoryRoot(), ".artifacts", "p02-registration-tests", Guid.NewGuid().ToString("N"));
    private Guid _specialtyId;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return;
        await _database.InitializeAsync();
        await using var context = _database.CreateContext();
        var specialty = Specialty.Create("تخصص اختبار", "Test Specialty").Value;
        context.Specialties.Add(specialty);
        await context.SaveChangesAsync();
        _specialtyId = specialty.Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = context.Database.GetConnectionString(),
            ["Auth:PrivateStorageRoot"] = _root
        }).Build();
        var environment = new TestEnvironment();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<TimeProvider>(_database.Clock);
        services.AddLogging(builder => builder.ClearProviders());
        services.AddApplication();
        services.AddInfrastructure(configuration);
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _database.DisposeAsync();
        // Synthetic files remain in the unique ignored artifacts folder; no pre-existing path is removed.
    }

    [SqlServerFact]
    public async Task Registration_PersistsPendingUserEvidence_WithoutRolesOrConfirmedEmail()
    {
        var result = await Register(Command());
        Assert.True(result.IsSuccess);
        Assert.Equal("PendingReview", result.Value.Status);
        Assert.Equal("DevelopmentFile", result.Value.EmailDeliveryStatus);
        Assert.True(result.Value.EmailConfirmationRequired);
        await using var context = _database.CreateContext();
        var user = Assert.Single(await context.Users.ToArrayAsync());
        Assert.False(user.EmailConfirmed);
        Assert.NotNull(user.PasswordHash);
        Assert.Equal("Synthetic Doctor", user.FullName);
        Assert.Empty(await context.UserRoles.ToArrayAsync());
        var registration = await context.DoctorRegistrations.Include(row => row.Documents).SingleAsync();
        Assert.Equal(user.Id, registration.UserId);
        Assert.Equal(_specialtyId, registration.SpecialtyId);
        Assert.Equal(DoctorRegistrationStatus.PendingReview, registration.Status);
        Assert.Null(registration.CreatedByUserId);
        Assert.Equal(4, registration.Documents.Count);
        Assert.All(registration.Documents, document =>
        {
            Assert.Null(document.CreatedByUserId);
            var path = Path.Combine(_root, "doctor-evidence", document.StorageKey);
            Assert.True(File.Exists(path));
            Assert.Equal(document.Length, new FileInfo(path).Length);
            Assert.Equal(document.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        });
        Assert.DoesNotContain("token", JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task DuplicateEmail_IsCaseInsensitive_AndDoesNotAddDocuments()
    {
        Assert.True((await Register(Command(email: "doctor@example.test"))).IsSuccess);
        var result = await Register(Command(email: "DOCTOR@example.test"));
        Assert.True(result.IsError);
        Assert.Equal("Account.EmailTaken", Assert.Single(result.Errors).Code);
        await using var context = _database.CreateContext();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(1, await context.DoctorRegistrations.CountAsync());
        Assert.Equal(4, DocumentCount());
    }

    [SqlServerFact]
    public async Task ConcurrentEmailRegistration_CommitsOnlyOneCompleteRegistration()
    {
        var results = await Task.WhenAll(Register(Command(email: "race@example.test")), Register(Command(email: "RACE@example.test")));
        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.Contains(results.Single(result => result.IsError).Errors, error => error.Code == "Account.EmailTaken");
        await using var context = _database.CreateContext();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(1, await context.DoctorRegistrations.CountAsync());
        Assert.Equal(4, DocumentCount());
    }

    [SqlServerFact]
    public async Task InvalidEvidence_WritesNoAccountOrDocuments()
    {
        var command = Command();
        command.Documents[0].Content.SetLength(0);
        var result = await Register(command);
        Assert.True(result.IsError);
        await AssertNoRegistration();
    }

    [SqlServerFact]
    public async Task WeakPassword_WritesNoAccountOrDocuments()
    {
        var result = await Register(Command() with { Password = "weak" });
        Assert.True(result.IsError);
        Assert.Contains(result.Errors, error => error.Field == "Password");
        await AssertNoRegistration();
    }

    [SqlServerFact]
    public async Task FileWriteFailure_RollsBackTheIdentityAccount()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "doctor-evidence"), "synthetic blocking file");
        await Assert.ThrowsAnyAsync<IOException>(() => Register(Command()));
        await AssertNoRegistration();
    }

    [SqlServerFact]
    public async Task EmailConfirmation_ValidatesProof_AndNeverApprovesTheDoctor()
    {
        var receipt = (await Register(Command())).Value;
        var mail = Directory.GetFiles(Path.Combine(_root, "development-emails"), "*.json").Single();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(mail));
        var query = QueryHelpers.ParseQuery(new Uri(json.RootElement.GetProperty("ConfirmationUrl").GetString()!).Query);
        await using var scope = _services!.CreateAsyncScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>();
        Assert.True((await confirmation.ConfirmAsync(receipt.UserId, "invalid", default)).IsError);
        Assert.True((await confirmation.ConfirmAsync(Guid.NewGuid(), query["token"].ToString(), default)).IsError);
        Assert.True((await confirmation.ConfirmAsync(receipt.UserId, query["token"].ToString(), default)).IsSuccess);
        Assert.True((await confirmation.ConfirmAsync(receipt.UserId, query["token"].ToString(), default)).IsSuccess);
        Assert.True((await confirmation.ConfirmAsync(receipt.UserId, "invalid", default)).IsError);
        await using var context = _database.CreateContext();
        Assert.True((await context.Users.SingleAsync()).EmailConfirmed);
        Assert.Equal(DoctorRegistrationStatus.PendingReview, (await context.DoctorRegistrations.SingleAsync()).Status);
        Assert.Empty(await context.UserRoles.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task Resend_OnlyWritesForAnExistingUnconfirmedEmail()
    {
        await using var scope = _services!.CreateAsyncScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>();
        await confirmation.ResendAsync("unknown@example.test", default);
        Assert.False(Directory.Exists(Path.Combine(_root, "development-emails")));
        var receipt = (await Register(Command())).Value;
        await confirmation.ResendAsync("doctor@example.test", default);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "development-emails")).Length);
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(receipt.UserId.ToString());
        Assert.NotNull(user);
    }

    [SqlServerFact]
    public async Task TrustedRequestActor_PersistsActorForeignKeys_WithoutAttributingSystemWork()
    {
        var creator = Guid.NewGuid();
        var modifier = Guid.NewGuid();
        await using (var setup = _database.CreateContext())
        {
            setup.Users.AddRange(new AppUser { Id = creator, FullName = "Synthetic actor one" },
                new AppUser { Id = modifier, FullName = "Synthetic actor two" });
            await setup.SaveChangesAsync();
        }
        var services = _services ?? throw new InvalidOperationException("Test services were not initialized.");
        var accessor = services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var specialty = Specialty.Create("تخصص actor", "Actor Test").Value;
        try
        {
            accessor.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            accessor.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, creator.ToString())], "TrustedTest"));
            await using (var scope = services.CreateAsyncScope())
            {
                var writer = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                writer.Specialties.Add(specialty);
                await writer.SaveChangesAsync();
            }
            accessor.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, modifier.ToString())], "TrustedTest"));
            await using (var scope = services.CreateAsyncScope())
            {
                var writer = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await writer.Specialties.SingleAsync(value => value.Id == specialty.Id);
                row.CreatedByUserId = modifier;
                row.LastModifiedAtUtc = DateTimeOffset.MinValue;
                await writer.SaveChangesAsync();
            }
            accessor.HttpContext = null;
            await using (var scope = services.CreateAsyncScope())
            {
                var writer = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                writer.Specialties.Add(Specialty.Create("تخصص system", "System Test").Value);
                await writer.SaveChangesAsync();
            }
            await using var reader = _database.CreateContext();
            var stored = await reader.Specialties.SingleAsync(value => value.Id == specialty.Id);
            Assert.Equal(creator, stored.CreatedByUserId);
            Assert.Equal(modifier, stored.LastModifiedByUserId);
            Assert.Null((await reader.Specialties.SingleAsync(value => value.NameEn == "System Test")).CreatedByUserId);
            Assert.Null((await reader.Specialties.SingleAsync(value => value.Id == _specialtyId)).CreatedByUserId);
        }
        finally { accessor.HttpContext = null; }
    }

    [SqlServerFact]
    public async Task InvalidConfirmationConfigurationWritesNoDoctorOrEvidence()
    {
        _services!.GetRequiredService<IConfiguration>()["Auth:EmailConfirmationUrl"] = "invalid-url";
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => Register(Command()));
        await AssertNoRegistration();
        Assert.False(Directory.Exists(Path.Combine(_root, "doctor-evidence")));
    }

    private RegisterDoctorCommand Command(string email = "doctor@example.test")
    {
        var uploads = Enum.GetValues<DoctorEvidenceKind>().Select(kind =>
        {
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nSynthetic graduation test evidence only.\n%%EOF");
            return new RegistrationUpload(kind, "evidence.pdf", "application/pdf", bytes.Length, new MemoryStream(bytes));
        }).ToArray();
        return new RegisterDoctorCommand("Synthetic Doctor", email, "Synthetic-Test1!", "01012345678",
            "TEST-123", _specialtyId, ProfessionalGrade.Specialist, uploads);
    }

    private async Task<ClinIQ.Domain.Common.Results.Result<DoctorRegistrationReceipt>> Register(RegisterDoctorCommand command)
    {
        await using var scope = _services!.CreateAsyncScope();
        try { return await scope.ServiceProvider.GetRequiredService<IDoctorRegistrationService>().RegisterAsync(command, default); }
        finally { foreach (var document in command.Documents) await document.Content.DisposeAsync(); }
    }

    private async Task AssertNoRegistration()
    {
        await using var context = _database.CreateContext();
        Assert.Equal(0, await context.Users.CountAsync());
        Assert.Equal(0, await context.DoctorRegistrations.CountAsync());
        Assert.Equal(0, DocumentCount());
    }

    private int DocumentCount() => Directory.Exists(Path.Combine(_root, "doctor-evidence"))
        ? Directory.GetFiles(Path.Combine(_root, "doctor-evidence"), "*", SearchOption.AllDirectories).Length : 0;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ClinIQ.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
