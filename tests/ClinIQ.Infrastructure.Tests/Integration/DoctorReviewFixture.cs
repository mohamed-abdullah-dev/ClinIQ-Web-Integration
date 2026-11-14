using System.Security.Claims;
using System.Text;
using ClinIQ.Application;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.RegisterDoctor;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

public sealed class DoctorReviewFixture : IAsyncLifetime
{
    public const string Password = "Synthetic-Review1!";
    public static readonly byte[] Evidence = Encoding.UTF8.GetBytes("%PDF-1.7\nSynthetic review evidence only.\n%%EOF");
    public SqlServerIntegrationFixture Database { get; } = new();
    public string PrivateRoot { get; } = Path.Combine(RepositoryRoot, ".artifacts", "doctor-review-tests", Guid.NewGuid().ToString("N"));
    public ServiceProvider Services { get; private set; } = null!;
    public string Connection { get; private set; } = null!;
    public Guid SpecialtyId { get; private set; }
    public AppUser Admin { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerIntegrationFixture.ConnectionVariable))) return;
        await Database.InitializeAsync();
        await using var context = Database.CreateContext();
        Connection = context.Database.GetConnectionString()!;
        var specialty = Specialty.Create("تخصص اختبار", "Synthetic specialty").Value;
        context.Specialties.Add(specialty); await context.SaveChangesAsync(); SpecialtyId = specialty.Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Connection, ["Auth:PrivateStorageRoot"] = PrivateRoot
        }).Build();
        var environment = new TestEnvironment(); var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration); services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment); services.AddSingleton<TimeProvider>(Database.Clock);
        services.AddLogging(builder => builder.ClearProviders()); services.AddApplication(); services.AddInfrastructure(configuration);
        Services = services.BuildServiceProvider(validateScopes: true);
        Admin = await UserAsync("Admin");
    }
    public async Task DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        await Database.DisposeAsync();
    }
    public async Task<AppUser> UserAsync(string? role, bool confirmed = true)
    {
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var email = $"review-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = confirmed, FullName = "Synthetic Review User" };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        if (role is not null)
        {
            if (!await roleManager.RoleExistsAsync(role)) Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        }
        return user;
    }
    public async Task<(DoctorRegistrationReceipt Receipt, string Email)> DoctorAsync(bool confirmed = true)
    {
        await using var scope = Services.CreateAsyncScope();
        var email = $"doctor-{Guid.NewGuid():N}@example.test";
        var uploads = new[] { DoctorEvidenceKind.PracticeLicense, DoctorEvidenceKind.SyndicateRegistration, DoctorEvidenceKind.Identity }
            .Select(kind => new RegistrationUpload(kind, "synthetic.pdf", "application/pdf", Evidence.Length, new MemoryStream(Evidence))).ToArray();
        try
        {
            var input = new RegisterDoctorCommand("Synthetic Doctor", email, Password, "01012345678", "SYNTHETIC",
                SpecialtyId, ProfessionalGrade.GeneralPractitioner, uploads);
            var result = await scope.ServiceProvider.GetRequiredService<IDoctorRegistrationService>().RegisterAsync(input, default);
            Assert.True(result.IsSuccess);
            if (confirmed) await ConfirmAsync(result.Value.UserId);
            return (result.Value, email);
        }
        finally { foreach (var upload in uploads) await upload.Content.DisposeAsync(); }
    }
    public async Task ConfirmAsync(Guid id)
    {
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await manager.FindByIdAsync(id.ToString()))!;
        Assert.True((await manager.ConfirmEmailAsync(user, await manager.GenerateEmailConfirmationTokenAsync(user))).Succeeded);
    }
    public async Task<T> AsAsync<T>(Guid? actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = actor is null ? null : new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Value.ToString())], "SyntheticTest"))
        };
        try { return await action(scope.ServiceProvider); }
        finally { accessor.HttpContext = null; }
    }
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ClinIQ.API";
        public string ContentRootPath { get; set; } = Path.Combine(RepositoryRoot, "src", "ClinIQ.API");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
