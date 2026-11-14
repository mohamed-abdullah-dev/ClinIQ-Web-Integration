using ClinIQ.Domain.Appointments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

// Older-schema migration tests must not call today's public directory/booking queries.
internal static class LegacyAttendanceSeed
{
    internal static async Task<(Guid Doctor, Appointment Appointment)> CreateAsync(DoctorReviewFixture fixture, string migration)
    {
        var schedule = new SchedulingTests(fixture); var (doctor, place) = await schedule.Place();
        var sessionDto = (await schedule.Extra(doctor, place.Id, schedule.Tomorrow, 18, 20)).Value;
        var patient = await new AppointmentTests(fixture).Patient();
        await using var db = fixture.Database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
        var session = await db.BookingSessions.SingleAsync(s => s.Id == sessionDto.Id);
        var profile = await db.DoctorProfiles.SingleAsync(p => p.UserId == doctor);
        var specialty = await db.Specialties.SingleAsync(s => s.Id == profile.SpecialtyId);
        var patientProfile = await db.PatientProfiles.SingleAsync(p => p.UserId == patient);
        var row = Appointment.Create(patientProfile.Id, patient, doctor,
            new(session.Id, place.Id, profile.Id, null, null, profile.ProfessionalName, profile.ProfessionalGrade,
                specialty.Id, specialty.NameAr, specialty.NameEn, place.Name, place.Address,
                session.LocalDate, session.StartsLocal, session.EndsLocal, session.TimeZoneId,
                session.StartsAtUtc, session.EndsAtUtc, session.BookingClosesAtUtc, place.ConsultationFee, place.Currency,
                "Synthetic Patient", "01012345678")).Value;
        Assert.True(session.Allocate(fixture.Database.Clock.GetUtcNow(), true).IsSuccess);
        db.Appointments.Add(row); await db.SaveChangesAsync(); return (doctor, row);
    }
}
