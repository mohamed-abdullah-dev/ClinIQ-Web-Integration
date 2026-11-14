using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Clinics;
using ClinIQ.Domain.Doctors;
using ClinIQ.Domain.PracticeLifecycle;
using ClinIQ.Domain.PracticeLocations;
using ClinIQ.Domain.Scheduling;
using Xunit;

namespace ClinIQ.Domain.Tests.PracticeLifecycle;

public sealed class LifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
    private static DoctorPracticeLocation Place()
    {
        var p = DoctorPracticeLocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PracticeLocationDetails.Create("Place", "Address", null, null).Value, ConsultationPrice.Create(100, "EGP").Value);
        var s = p.Submit(Guid.NewGuid(), p.UserId, [new(PracticeLocationEvidenceKind.FacilityLicense, $"{Guid.NewGuid():N}.pdf", "test.pdf", "application/pdf", 10, new string('A', 64))]).Value;
        Assert.True(p.Review(s, PracticeLocationStatus.Approved, "Verified", Guid.NewGuid(), Start, true, true).IsSuccess); return p;
    }
    private static ClinicBranch Branch()
    {
        var request = ClinicRegistrationRequest.Create(Guid.NewGuid(), Guid.NewGuid(), null,
            new("Clinic", "Description", "Branch", "Address", null, null, "01012345678", "Authority", "License", "Operator", ClinicApplicantCapacity.LicensedOperator, ManagementAuthorityBasis.RecordedEvidence)).Value;
        var s = request.Submit(Guid.NewGuid(), request.ApplicantUserId, new[] { ClinicEvidenceKind.FacilityLicense, ClinicEvidenceKind.ApplicantIdentity }
            .Select(k => new ClinicEvidenceMetadata(k, $"{Guid.NewGuid():N}.pdf", "test.pdf", "application/pdf", 10, new string('A', 64))).ToArray()).Value;
        var clinic = Guid.NewGuid(); var branch = Guid.NewGuid();
        Assert.True(request.Review(s, ClinicApplicationStatus.Approved, "Verified", Guid.NewGuid(), Start, true, true, true, true, clinic, branch).IsSuccess);
        return ClinicBranch.FromApprovedSubmission(branch, clinic, s).Value;
    }
    [Theory]
    [InlineData(1)] [InlineData(100)] [InlineData(10000)]
    public void OutstandingCountPreventsDepartureAndClosureWithoutChangingOperationalAccess(int count)
    {
        var p = Place(); var b = Branch(); Assert.True(p.BeginDeparture().Value); Assert.True(b.BeginClosure().Value);
        Assert.True(p.IsActive); Assert.True(b.IsActive); Assert.False(p.BeginDeparture().Value); Assert.False(b.BeginClosure().Value);
        Assert.Equal(LifecycleErrors.Outstanding, Assert.Single(p.FinalizeDeparture(count).Errors));
        Assert.Equal(LifecycleErrors.Outstanding, Assert.Single(b.FinalizeClosure(count).Errors));
        Assert.Equal(PracticeLifecycleState.PendingDeparture, p.LifecycleState); Assert.Equal(BranchLifecycleState.PendingClosure, b.LifecycleState);
    }
    [Fact]
    public void EmptyScopeRequiresBeginThenFinalizesIdempotentlyWithoutDisablingOtherResources()
    {
        var p = Place(); var b = Branch(); var other = Place();
        Assert.Equal(LifecycleErrors.NotPending, Assert.Single(p.FinalizeDeparture(0).Errors));
        Assert.Equal(LifecycleErrors.NotPending, Assert.Single(b.FinalizeClosure(0).Errors));
        p.BeginDeparture(); b.BeginClosure(); Assert.True(p.FinalizeDeparture(0).Value); Assert.True(b.FinalizeClosure(0).Value);
        Assert.False(p.IsActive); Assert.False(b.IsActive); Assert.True(other.IsActive);
        Assert.False(p.FinalizeDeparture(0).Value); Assert.False(b.FinalizeClosure(0).Value);
        Assert.True(p.BeginDeparture().IsError); Assert.True(b.BeginClosure().IsError);
    }
    [Theory]
    [InlineData(EmergencyReasonCategory.Illness)] [InlineData(EmergencyReasonCategory.UnusablePremises)]
    public void EmergencyRequiresAnIndependentReasonedDecisionAndIsImmutableAfterDecision(EmergencyReasonCategory category)
    {
        var actor = Guid.NewGuid(); var request = OperationalEmergency.Create(OperationalScopeKind.Location, Guid.NewGuid(), actor, category, "  Reason  ", Start).Value;
        Assert.Equal("Reason", request.Reason); Assert.Equal(EmergencyRequestStatus.Pending, request.Status);
        Assert.Equal(LifecycleErrors.SelfReview, Assert.Single(request.Decide(true, actor, "Review", new string('A', 64), new byte[8], 1, Start).Errors));
        Assert.True(request.Decide(true, Guid.NewGuid(), " ", new string('A', 64), new byte[8], 1, Start).IsError);
        Assert.True(request.Decide(true, Guid.NewGuid(), "Approved", new string('A', 64), new byte[8], 1, Start).Value);
        Assert.Equal(1, request.CancelledCount); Assert.Equal(EmergencyRequestStatus.Approved, request.Status);
        Assert.True(request.Decide(false, Guid.NewGuid(), "Overwrite", new string('B', 64), new byte[8], 0, Start).IsError);
    }
    [Theory]
    [InlineData(AppointmentStatus.Confirmed)] [InlineData(AppointmentStatus.CheckedIn)]
    public void ApprovedEmergencyPreservesArrivalAndCancelsOnlyItsOwnScopeEvenAfterSessionEnd(AppointmentStatus status)
    {
        var a = Booking();
        if (status == AppointmentStatus.CheckedIn) Assert.True(a.RecordAttendance(new(a.PatientId, AttendanceAction.CheckIn, "unused"), a.DoctorUserId, true, false, Start, 7).IsSuccess);
        var request = OperationalEmergency.Create(OperationalScopeKind.Location, a.LocationId, a.DoctorUserId, EmergencyReasonCategory.Illness, "Unable to continue", Start).Value;
        Assert.True(a.CancelForEmergency(request, null, Start.AddDays(2)).IsError);
        var reviewer = Guid.NewGuid(); request.Decide(true, reviewer, "Verified disruption", new string('A', 64), new byte[8], 1, Start.AddDays(2));
        Assert.True(a.CancelForEmergency(request, null, Start.AddDays(2)).IsSuccess);
        Assert.Equal(AppointmentStatus.Cancelled, a.Status); Assert.Equal(status, a.StatusBeforeCancellation);
        Assert.Equal(reviewer, a.CancelledByUserId); Assert.Equal("AdminEmergencyClosure", a.CancellationCategory);
        Assert.Equal(status == AppointmentStatus.CheckedIn ? Start : (DateTimeOffset?)null, a.ArrivedAtUtc);
        Assert.Equal(status == AppointmentStatus.CheckedIn ? 7 : (int?)null, a.QueueNumber);
        Assert.True(a.CancelForEmergency(request, null, Start.AddDays(2)).IsError);
        Assert.True(Booking().CancelForEmergency(request, null, Start).IsError);
    }
    [Fact]
    public void CompletedVisitAndCapacityFactsSurviveScopeRetirement()
    {
        var a = Booking(); a.RecordAttendance(new(a.PatientId, AttendanceAction.CheckIn, "unused"), a.DoctorUserId, true, false, Start, 1);
        a.RecordAttendance(new(a.PatientId, AttendanceAction.Complete, "unused", CompletedAtUtc: Start.AddMinutes(10)), a.DoctorUserId, true, false, Start.AddMinutes(20), 1);
        var request = OperationalEmergency.Create(OperationalScopeKind.Location, a.LocationId, a.DoctorUserId, EmergencyReasonCategory.UnusablePremises, "Reason", Start).Value;
        request.Decide(true, Guid.NewGuid(), "Approved", new string('A', 64), new byte[8], 0, Start.AddHours(3));
        Assert.True(a.CancelForEmergency(request, null, Start.AddHours(3)).IsError); Assert.Equal(AppointmentStatus.Completed, a.Status);
        var session = BookingSession.Create(a.LocationId, a.DoctorProfileId, a.DoctorUserId,
            new(a.LocalDate, a.StartsLocal, a.EndsLocal, Start, Start.AddHours(2)), 10, 0, Start.AddMinutes(-1)).Value;
        session.Allocate(Start, true); session.IssueQueueNumber();
        Assert.True(session.RetireForLifecycle(Guid.NewGuid(), "Scope ended", Start.AddDays(1), 1).IsError);
        Assert.True(session.RetireForLifecycle(Guid.NewGuid(), "Scope ended", Start.AddDays(1), 0).Value);
        Assert.Equal(1, session.AllocatedCount); Assert.Equal(1, session.LastQueueNumber); Assert.Equal(BookingSessionStatus.Cancelled, session.Status);
    }
    private static Appointment Booking() => Appointment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "Doctor", ProfessionalGrade.GeneralPractitioner, Guid.NewGuid(), "تخصص", "Specialty", "Place", "Address",
            new(2026, 11, 1), new(18, 0), new(20, 0), "Africa/Cairo", Start, Start.AddHours(2), Start.AddHours(2), 100, "EGP", "Patient", "01012345678")).Value;
}
