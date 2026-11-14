using ClinIQ.API.Infrastructure;
using ClinIQ.Application;
using ClinIQ.Infrastructure;
using ClinIQ.Infrastructure.Data.Seeding;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication;

const string seedFlag = "--seed-specialties";
const string adminFlag = "--bootstrap-admin";
var seedSpecialties = args.Contains(seedFlag, StringComparer.Ordinal);
var bootstrapAdmin = args.Contains(adminFlag, StringComparer.Ordinal);
if (seedSpecialties && bootstrapAdmin)
{
    Console.Error.WriteLine("Run one maintenance command at a time.");
    Environment.ExitCode = 1;
    return;
}
// No credential arguments or configuration overrides are accepted by the operator command.
if (bootstrapAdmin && args.Any(argument => argument != adminFlag))
{
    Console.Error.WriteLine("Admin bootstrap accepts only --bootstrap-admin. Enter credentials interactively.");
    Environment.ExitCode = 1;
    return;
}
var hostArguments = args.Where(argument => argument != seedFlag && argument != adminFlag).ToArray();
var builder = WebApplication.CreateBuilder(hostArguments);

if (seedSpecialties && !builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine("The specialty seed command is available only in Development.");
    Environment.ExitCode = 1;
    return;
}

if (bootstrapAdmin && !builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine("Admin bootstrap is available only in Development.");
    Environment.ExitCode = 1;
    return;
}

builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(SessionAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ClinicWeb", policy => policy.RequireAuthenticatedUser().RequireClaim("client", "Web"));
    options.AddPolicy("PatientMobile", policy => policy.RequireAuthenticatedUser().RequireClaim("client", "Mobile").RequireRole("Patient"));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "ClinIQ.Csrf" : "__Host-ClinIQ.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddCors(options => options.AddPolicy("clients", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-recovery", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue<int?>("Auth:Recovery:PermitLimit") ?? 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("auth-sessions", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue<int?>("Auth:Sessions:PermitLimit") ?? 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("auth-registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue<int?>("Auth:RateLimit:PermitLimit") ?? 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

if (bootstrapAdmin)
{
    await using (app)
        Environment.ExitCode = await AdminBootstrapConsole.RunAsync(app.Services);
    return;
}

if (seedSpecialties)
{
    await using (app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SpecialtySeeder>().SeedAsync();
        Environment.ExitCode = result.Match(
            summary =>
            {
                Console.WriteLine($"SPECIALTY_SEED: added={summary.Added}; existing={summary.Existing}");
                return 0;
            },
            errors =>
            {
                foreach (var error in errors)
                    Console.Error.WriteLine($"{error.Code}: {error.Description}");
                return 1;
            });
    }
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseCors("clients");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/auth") ||
        context.Request.Path.StartsWithSegments("/api/doctors/me/scheduling") ||
        context.Request.Path.StartsWithSegments("/api/work-invitations") ||
        context.Request.Path.StartsWithSegments("/api/workforce") ||
        context.Request.Path.StartsWithSegments("/api/admin/doctor-registrations") ||
        context.Request.Path.StartsWithSegments("/api/admin/specialties") ||
        context.Request.Path.StartsWithSegments("/api/admin/doctor-professional-changes") ||
        context.Request.Path.StartsWithSegments("/api/admin/practice-locations") ||
        context.Request.Path.StartsWithSegments("/api/premises-corrections") ||
        context.Request.Path.StartsWithSegments("/api/admin/premises-corrections") ||
        context.Request.Path.StartsWithSegments("/api/clinic-applications") ||
        context.Request.Path.StartsWithSegments("/api/managed-clinics") ||
        context.Request.Path.StartsWithSegments("/api/admin/clinic-applications") ||
        context.Request.Path.StartsWithSegments("/api/doctors/me/practice-locations") ||
        context.Request.Path.StartsWithSegments("/api/doctors/me/professional-changes") ||
        context.Request.Path.StartsWithSegments("/api/doctors/me/professional-profile") ||
        context.Request.Path.StartsWithSegments("/api/patients/me/profile"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<WebSessionCsrfMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.Run();
