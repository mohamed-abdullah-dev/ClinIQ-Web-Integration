using ClinIQ.Domain.Appointments;
using ClinIQ.Domain.Doctors;
using Xunit;

namespace ClinIQ.Domain.Tests.Appointments;

public sealed class AppointmentTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
    private static AppointmentSelection Selection => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "Doctor", ProfessionalGrade.GeneralPractitioner,
        Guid.NewGuid(), "تخصص", "Specialty", "Place", "Address", new(2026, 11, 1), new(18, 0), new(20, 0), "Africa/Cairo", Start, Start.AddHours(2), Start.AddHours(1), 100, "EGP", "Patient", "01012345678");
    [Fact]
    public void CancellationIsOwnedPreservesFactsAndIsIdempotentAfterTheCutoff()
    {
        var user = Guid.NewGuid(); var row = Appointment.Create(Guid.NewGuid(), user, Guid.NewGuid(), Selection).Value;
        Assert.Equal(AppointmentErrors.NotFound, Assert.Single(row.CancelByPatient(Guid.NewGuid(), "Cancel", Start).Errors));
        Assert.Equal(AppointmentStatus.Confirmed, row.Status);
        Assert.True(row.CancelByPatient(user, "Cannot attend", Start.AddMinutes(90)).IsSuccess);
        Assert.Equal(AppointmentStatus.Confirmed, row.StatusBeforeCancellation); Assert.False(row.ConsumesCapacity);
        Assert.True(row.CancelByPatient(user, "Second reason", Start.AddDays(1)).IsSuccess);
        Assert.Equal("Cannot attend", row.CancellationReason); Assert.Equal(Start.AddMinutes(90), row.CancelledAtUtc);
    }
    [Fact]
    public void SessionEndStopsPatientChangesAndFailedCancellationDoesNotMutate()
    {
        var user = Guid.NewGuid(); var row = Appointment.Create(Guid.NewGuid(), user, Guid.NewGuid(), Selection).Value;
        Assert.Equal(AppointmentErrors.Reason, Assert.Single(row.CancelByPatient(user, " ", Start).Errors));
        Assert.Equal(AppointmentErrors.CannotChange, Assert.Single(row.CancelByPatient(user, "Cancel", Start.AddHours(2)).Errors));
        Assert.Equal(AppointmentErrors.CannotChange, Assert.Single(row.Reschedule(user, Start.AddHours(2)).Errors));
        Assert.Equal(AppointmentStatus.Confirmed, row.Status); Assert.True(row.ConsumesCapacity);
    }
    [Fact]
    public void ReschedulingIsTerminalAndKeepsTheAcceptedSelection()
    {
        var user = Guid.NewGuid(); var selection = Selection; var row = Appointment.Create(Guid.NewGuid(), user, Guid.NewGuid(), selection).Value;
        Assert.True(row.Reschedule(user, Start).IsSuccess); Assert.Equal(AppointmentStatus.Rescheduled, row.Status);
        Assert.Equal(selection, row.Selection); Assert.False(row.ConsumesCapacity);
        Assert.Equal(AppointmentErrors.CannotChange, Assert.Single(row.CancelByPatient(user, "Cancel", Start).Errors));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void InvalidPricesCannotCreateAnAppointment(decimal price) => Assert.True(Appointment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Selection with { Fee = price }).IsError);
}
