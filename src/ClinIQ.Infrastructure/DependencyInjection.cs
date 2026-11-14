using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Infrastructure.Data;
using ClinIQ.Infrastructure.Data.Interceptors;
using ClinIQ.Infrastructure.Data.Seeding;
using ClinIQ.Infrastructure.Identity;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Infrastructure.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ClinIQ.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ClinIQ.Application.Features.Subscriptions.IDoctorSubscriptionService, ClinIQ.Infrastructure.Subscriptions.DoctorSubscriptionService>();
        services.AddScoped<ClinIQ.Application.Features.PracticeLinks.IPracticeLinkService, ClinIQ.Infrastructure.PracticeLinks.PracticeLinkService>();
        services.AddScoped<ClinIQ.Application.Features.PracticeLifecycle.IPracticeLifecycleService, ClinIQ.Infrastructure.PracticeLifecycle.PracticeLifecycleService>();
        services.AddScoped<ClinIQ.Application.Features.VisitReviews.IVisitReviewService,ClinIQ.Infrastructure.VisitReviews.VisitReviewService>();
        services.AddOptions<ClinIQ.Infrastructure.Notifications.FeedbackNotificationOptions>()
            .Configure<IHostEnvironment>((o, e) => { o.PushMode = e.IsDevelopment() ? "DevelopmentFile" : "Disabled"; o.EmailMode = e.IsDevelopment() ? "DevelopmentFile" : "Disabled"; configuration.GetSection("FeedbackNotifications").Bind(o); })
            .Validate(o => o.Valid(), "Invalid feedback notification transport configuration.")
            .Validate<IHostEnvironment>((o, e) => e.IsDevelopment() || (o.PushMode != "DevelopmentFile" && o.EmailMode != "DevelopmentFile"), "DevelopmentFile notification transport is available only in Development.").ValidateOnStart();
        services.AddScoped<ClinIQ.Application.Features.Notifications.IPushDeviceService, ClinIQ.Infrastructure.Notifications.PushDeviceService>();
        services.AddScoped<ClinIQ.Application.Features.Notifications.IFeedbackDispatcher, ClinIQ.Infrastructure.Notifications.FeedbackDispatcher>();
        services.AddScoped<ClinIQ.Application.Features.Notifications.INotificationAdministrationService, ClinIQ.Infrastructure.Notifications.NotificationAdministrationService>();
        services.AddSingleton<ClinIQ.Application.Features.Notifications.IFeedbackNotificationTransport, ClinIQ.Infrastructure.Notifications.FeedbackNotificationTransport>();
        services.AddHostedService<ClinIQ.Infrastructure.Notifications.FeedbackNotificationWorker>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddSingleton<RegistrationStorage>();
        services.AddOptions<EmailConfirmationOptions>()
            .Configure<IHostEnvironment>((options, environment) =>
                options.Url = configuration["Auth:EmailConfirmationUrl"] ??
                    (environment.IsDevelopment() ? "http://localhost:5173/confirm-email" : null))
            .Validate(options => EmailConfirmationOptions.IsValidUrl(options.Url),
                "Configure a trusted absolute Auth:EmailConfirmationUrl without credentials, query, or fragment.")
            .ValidateOnStart();
        services.AddDataProtection().SetApplicationName("ClinIQ");
        services.AddOptions<DoctorResubmissionOptions>()
            .Configure<IHostEnvironment>((options, environment) => options.Url = configuration["Auth:DoctorResubmissionUrl"] ??
                (environment.IsDevelopment() ? "http://localhost:5173/doctor/resubmit" : null))
            .Validate(options => EmailConfirmationOptions.IsValidUrl(options.Url),
                "Configure a trusted absolute Auth:DoctorResubmissionUrl without credentials, query, or fragment.")
            .Validate<IHostEnvironment>((options, environment) => environment.IsDevelopment() || options.Url is null ||
                (Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"),
                "Auth:DoctorResubmissionUrl must use HTTPS outside Development.")
            .ValidateOnStart();
        services.AddScoped<ClinIQ.Application.Features.DoctorResubmissions.IDoctorResubmissionService, DoctorResubmissionService>();
        services.AddOptions<KeyManagementOptions>().Configure<RegistrationStorage, ILoggerFactory>((options, storage, loggerFactory) =>
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(storage.Root, "keys")), loggerFactory));
        services.AddScoped<DoctorRegistrationService>();
        services.AddScoped<IDoctorRegistrationService>(provider => provider.GetRequiredService<DoctorRegistrationService>());
        services.AddScoped<IPatientRegistrationService, PatientRegistrationService>();
        services.AddScoped<ClinIQ.Application.Features.Patients.IPatientProfileService, PatientProfileService>();
        services.AddScoped<ClinIQ.Application.Features.Specialties.ISpecialtyAdministrationService,
            ClinIQ.Infrastructure.Specialties.SpecialtyAdministrationService>();
        services.AddScoped<FirstAdminBootstrapper>();
        services.AddSingleton<ClinIQ.Infrastructure.Scheduling.CairoScheduleClock>();
        services.AddScoped<ClinIQ.Application.Features.Scheduling.ISchedulingService, ClinIQ.Infrastructure.Scheduling.SchedulingService>();
        services.AddScoped<ClinIQ.Infrastructure.Invitations.WorkforceAccess>();
        services.AddScoped<ClinIQ.Application.Features.Attendance.IAttendanceService, ClinIQ.Infrastructure.Attendance.AttendanceService>();
        services.AddScoped<ClinIQ.Application.Features.AttendanceFeedback.IAttendanceFeedbackService, ClinIQ.Infrastructure.AttendanceFeedback.AttendanceFeedbackService>();
        services.AddScoped<ClinIQ.Infrastructure.Appointments.BookingQuoteProtector>();
        services.AddScoped<ClinIQ.Infrastructure.Appointments.BookingNoticeDelivery>();
        services.AddScoped<ClinIQ.Application.Features.Appointments.IAppointmentService, ClinIQ.Infrastructure.Appointments.AppointmentService>();
        services.AddScoped<ClinIQ.Application.Features.Directory.IPublicDirectoryService, ClinIQ.Infrastructure.PublicDirectory.PublicDirectoryService>();
        services.AddScoped<ClinIQ.Infrastructure.Invitations.WorkMessageDelivery>();
        services.AddScoped<ClinIQ.Application.Features.Invitations.IWorkInvitationService, ClinIQ.Infrastructure.Invitations.WorkInvitationService>();
        services.AddScoped<ClinIQ.Application.Features.Invitations.IReceptionistRegistrationService, ClinIQ.Infrastructure.Invitations.ReceptionistRegistrationService>();
        services.AddScoped<ClinIQ.Application.Features.Invitations.IWorkforceService, ClinIQ.Infrastructure.Invitations.WorkforceService>();
        services.AddOptions<ClinIQ.Infrastructure.Invitations.WorkMessageOptions>()
            .Configure<IHostEnvironment>((o, e) => o.Url = configuration["Invitations:Url"] ?? (e.IsDevelopment() ? "http://localhost:5173/invitations" : null))
            .Validate(o => EmailConfirmationOptions.IsValidUrl(o.Url), "Configure a trusted Invitations:Url without credentials, query or fragment.")
            .Validate<IHostEnvironment>((o, e) => e.IsDevelopment() || o.Url is null || (Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"), "Invitations:Url must use HTTPS outside Development.")
            .ValidateOnStart();
        services.AddScoped<ClinIQ.Application.Features.PremisesCorrections.IPremisesCorrectionService,
            ClinIQ.Infrastructure.PremisesCorrections.PremisesCorrectionService>();
        services.AddScoped<ClinIQ.Application.Features.Clinics.IClinicService, ClinIQ.Infrastructure.Clinics.ClinicService>();
        services.AddScoped<ClinIQ.Application.Features.Clinics.Commands.IClinicApplicantService, ClinIQ.Infrastructure.Clinics.ClinicApplicantService>();
        services.AddScoped<ClinIQ.Application.Features.PracticeLocations.IPracticeLocationService,
            ClinIQ.Infrastructure.PracticeLocations.PracticeLocationService>();
        services.AddScoped<ClinIQ.Application.Features.DoctorReviews.IDoctorReviewService, DoctorReviewService>();
        services.AddScoped<ClinIQ.Application.Features.DoctorProfessionalChanges.IProfessionalChangeService,
            ClinIQ.Infrastructure.Doctors.ProfessionalChangeService>();
        services.AddScoped<AccountEmailConfirmationService>();
        services.AddScoped<IEmailConfirmationService>(provider => provider.GetRequiredService<AccountEmailConfirmationService>());
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IPasswordRecoveryService, AccountPasswordRecoveryService>();
        services.AddOptions<PasswordResetTokenProviderOptions>();

        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            options.UseSqlServer(connectionString);
            options.AddInterceptors(provider.GetRequiredService<AuditableEntityInterceptor>());
        });
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<SpecialtySeeder>();

        services.AddIdentityCore<AppUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders()
        .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);

        return services;
    }
}
