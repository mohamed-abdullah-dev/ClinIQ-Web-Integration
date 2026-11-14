using ClinIQ.Application.Common.Interfaces;
using ClinIQ.Domain.Common;
using ClinIQ.Domain.Accounts;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.Specialties;
using ClinIQ.Domain.Patients;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Subscriptions;
using ClinIQ.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ClinIQ.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IPublisher publisher)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    private bool _isSaving;
    public DbSet<DoctorProTrial> DoctorProTrials => Set<DoctorProTrial>();
    public DbSet<ClinIQ.Domain.PracticeLinks.PracticeLinkRequest> PracticeLinkRequests => Set<ClinIQ.Domain.PracticeLinks.PracticeLinkRequest>();
    private bool _saveFailed;
    public DbSet<ClinIQ.Domain.PracticeLifecycle.OperationalScopeRecord> OperationalScopeRecords => Set<ClinIQ.Domain.PracticeLifecycle.OperationalScopeRecord>();
    public DbSet<ClinIQ.Domain.PracticeLifecycle.OperationalEmergency> OperationalEmergencies => Set<ClinIQ.Domain.PracticeLifecycle.OperationalEmergency>();
    public DbSet<ClinIQ.Domain.PracticeLifecycle.EmergencyCancellationRecord> EmergencyCancellationRecords => Set<ClinIQ.Domain.PracticeLifecycle.EmergencyCancellationRecord>();
    public DbSet<ClinIQ.Domain.VisitReviews.VisitReview> VisitReviews => Set<ClinIQ.Domain.VisitReviews.VisitReview>();
    public DbSet<ClinIQ.Domain.VisitReviews.VisitReviewAudit> VisitReviewAudits => Set<ClinIQ.Domain.VisitReviews.VisitReviewAudit>();
    public DbSet<ClinIQ.Domain.Notifications.PushDevice> PushDevices => Set<ClinIQ.Domain.Notifications.PushDevice>();
    public DbSet<ClinIQ.Domain.Notifications.FeedbackNotice> FeedbackNotices => Set<ClinIQ.Domain.Notifications.FeedbackNotice>();
    public DbSet<ClinIQ.Domain.Notifications.FeedbackDelivery> FeedbackDeliveries => Set<ClinIQ.Domain.Notifications.FeedbackDelivery>();
    public DbSet<ClinIQ.Domain.AttendanceFeedback.AttendanceFeedbackRequest> AttendanceFeedbackRequests => Set<ClinIQ.Domain.AttendanceFeedback.AttendanceFeedbackRequest>();
    public DbSet<ClinIQ.Domain.AttendanceFeedback.AttendanceFeedbackResponse> AttendanceFeedbackResponses => Set<ClinIQ.Domain.AttendanceFeedback.AttendanceFeedbackResponse>();
    public DbSet<ClinIQ.Domain.Appointments.Appointment> Appointments => Set<ClinIQ.Domain.Appointments.Appointment>();
    public DbSet<ClinIQ.Domain.Appointments.AttendanceRecord> AttendanceRecords => Set<ClinIQ.Domain.Appointments.AttendanceRecord>();
    public DbSet<ClinIQ.Domain.Appointments.AppointmentOperation> AppointmentOperations => Set<ClinIQ.Domain.Appointments.AppointmentOperation>();
    public DbSet<ClinIQ.Domain.Appointments.BookingNotice> BookingNotices => Set<ClinIQ.Domain.Appointments.BookingNotice>();
    public DbSet<ClinIQ.Domain.Scheduling.ScheduleRule> ScheduleRules => Set<ClinIQ.Domain.Scheduling.ScheduleRule>();
    public DbSet<ClinIQ.Domain.Scheduling.BookingSession> BookingSessions => Set<ClinIQ.Domain.Scheduling.BookingSession>();
    public DbSet<ClinIQ.Domain.Scheduling.ScheduleException> ScheduleExceptions => Set<ClinIQ.Domain.Scheduling.ScheduleException>();
    public DbSet<ClinIQ.Domain.Invitations.WorkInvitation> WorkInvitations => Set<ClinIQ.Domain.Invitations.WorkInvitation>();
    public DbSet<ClinIQ.Domain.Invitations.ReceptionistAssignment> ReceptionistAssignments => Set<ClinIQ.Domain.Invitations.ReceptionistAssignment>();
    public DbSet<ClinIQ.Domain.Invitations.PracticeFeeChange> PracticeFeeChanges => Set<ClinIQ.Domain.Invitations.PracticeFeeChange>();

    public DbSet<ClinIQ.Domain.PremisesCorrections.PremisesCorrection> PremisesCorrections => Set<ClinIQ.Domain.PremisesCorrections.PremisesCorrection>();
    public DbSet<ClinIQ.Domain.PremisesCorrections.PremisesCorrectionDocument> PremisesCorrectionDocuments => Set<ClinIQ.Domain.PremisesCorrections.PremisesCorrectionDocument>();
    public DbSet<ClinIQ.Domain.PremisesCorrections.PremisesCorrectionEvidenceAccess> PremisesCorrectionEvidenceAccesses => Set<ClinIQ.Domain.PremisesCorrections.PremisesCorrectionEvidenceAccess>();

    public DbSet<Specialty> Specialties => Set<Specialty>();
    public DbSet<SpecialtyCatalogChange> SpecialtyCatalogChanges => Set<SpecialtyCatalogChange>();
    public DbSet<PatientProfile> PatientProfiles => Set<PatientProfile>();
    public DbSet<DoctorRegistration> DoctorRegistrations => Set<DoctorRegistration>();
    public DbSet<DoctorProfile> DoctorProfiles => Set<DoctorProfile>();
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<ClinicBranch> ClinicBranches => Set<ClinicBranch>();
    public DbSet<ClinicManagementMembership> ClinicManagementMemberships => Set<ClinicManagementMembership>();
    public DbSet<ClinicRegistrationRequest> ClinicRegistrationRequests => Set<ClinicRegistrationRequest>();
    public DbSet<ClinicRegistrationSubmission> ClinicRegistrationSubmissions => Set<ClinicRegistrationSubmission>();
    public DbSet<ClinicRegistrationDocument> ClinicRegistrationDocuments => Set<ClinicRegistrationDocument>();
    public DbSet<ClinicEvidenceAccess> ClinicEvidenceAccesses => Set<ClinicEvidenceAccess>();
    public DbSet<DoctorPracticeLocation> DoctorPracticeLocations => Set<DoctorPracticeLocation>();
    public DbSet<PracticeLocationSubmission> PracticeLocationSubmissions => Set<PracticeLocationSubmission>();
    public DbSet<PracticeLocationDocument> PracticeLocationDocuments => Set<PracticeLocationDocument>();
    public DbSet<PracticeLocationEvidenceAccess> PracticeLocationEvidenceAccesses => Set<PracticeLocationEvidenceAccess>();
    public DbSet<DoctorProfessionalChange> DoctorProfessionalChanges => Set<DoctorProfessionalChange>();
    public DbSet<ProfessionalChangeDocument> ProfessionalChangeDocuments => Set<ProfessionalChangeDocument>();
    public DbSet<ProfessionalChangeEvidenceAccess> ProfessionalChangeEvidenceAccesses => Set<ProfessionalChangeEvidenceAccess>();
    public DbSet<DoctorRegistrationReview> DoctorRegistrationReviews => Set<DoctorRegistrationReview>();
    public DbSet<DoctorEvidenceAccess> DoctorEvidenceAccesses => Set<DoctorEvidenceAccess>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<SessionRefreshToken> SessionRefreshTokens => Set<SessionRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in builder.Model.GetEntityTypes().ToArray())
        {
            if (typeof(Entity).IsAssignableFrom(entityType.ClrType))
                builder.Entity(entityType.ClrType).Ignore(nameof(Entity.DomainEvents));
            if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                builder.Entity(entityType.ClrType).HasOne(typeof(AppUser)).WithMany()
                    .HasForeignKey(nameof(AuditableEntity.CreatedByUserId)).OnDelete(DeleteBehavior.Restrict);
                builder.Entity(entityType.ClrType).HasOne(typeof(AppUser)).WithMany()
                    .HasForeignKey(nameof(AuditableEntity.LastModifiedByUserId)).OnDelete(DeleteBehavior.Restrict);
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_saveFailed)
            throw new InvalidOperationException("This context had a failed save. Dispose it and retry the operation in a new scope.");

        if (_isSaving)
            throw new InvalidOperationException("Domain event handlers must not call SaveChanges.");

        _isSaving = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchDomainEventsAsync(cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch
        {
            // Events were consumed before saving; retrying this instance could omit handler work.
            _saveFailed = true;
            throw;
        }
        finally
        {
            _isSaving = false;
        }
    }

    public override int SaveChanges() =>
        throw new NotSupportedException("Use SaveChangesAsync to include domain event handling.");

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync to include domain event handling.");

    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        // Handlers may update tracked data. External notifications need a post-commit policy.
        for (var batch = 0; ; batch++)
        {
            var entities = ChangeTracker.Entries<Entity>()
                .Select(entry => entry.Entity)
                .Where(entity => entity.DomainEvents.Count > 0)
                .ToArray();

            if (entities.Length == 0) return;

            if (batch == 32)
                throw new InvalidOperationException("Possible domain event cycle detected.");

            var events = entities.SelectMany(entity => entity.DomainEvents).ToArray();
            foreach (var entity in entities) entity.ClearDomainEvents();
            foreach (var domainEvent in events)
                await publisher.Publish(domainEvent, cancellationToken);
        }
    }
}
