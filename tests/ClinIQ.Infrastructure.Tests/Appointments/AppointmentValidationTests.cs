using ClinIQ.Application.Features.Appointments;
using ClinIQ.Application.Features.Appointments.Commands;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Appointments;

public sealed class AppointmentValidationTests
{
    private static BookingConfirmation Valid => new(Guid.NewGuid(), "protected-quote", Guid.NewGuid(), true, true, false);
    [Fact]
    public void ConfirmationRequiresRealIdsABoundedProofAndExplicitAcceptance()
    {
        var validator = new BookingConfirmationValidator(); Assert.True(validator.Validate(Valid).IsValid);
        foreach (var input in new[] { Valid with { SessionId = Guid.Empty }, Valid with { RequestId = Guid.Empty }, Valid with { AcceptPrice = false }, Valid with { ConfirmContact = false }, Valid with { QuoteToken = "" }, Valid with { QuoteToken = new string('q', 16385) } })
            Assert.False(validator.Validate(input).IsValid);
    }
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(100001, 20)]
    public void ListsAreBounded(int page, int size) => Assert.False(new ListAppointmentsValidator().Validate(new ListAppointmentsQuery(page, size)).IsValid);
    [Fact]
    public void CancellationAndRescheduleCannotSkipOwnershipReferenceReasonOrVersion()
    {
        Assert.False(new CancelAppointmentValidator().Validate(new CancelAppointmentCommand(Guid.Empty, Guid.NewGuid(), "Reason")).IsValid);
        Assert.False(new CancelAppointmentValidator().Validate(new CancelAppointmentCommand(Guid.NewGuid(), Guid.Empty, "Reason")).IsValid);
        Assert.False(new CancelAppointmentValidator().Validate(new CancelAppointmentCommand(Guid.NewGuid(), Guid.NewGuid(), " ")).IsValid);
        Assert.False(new RescheduleAppointmentValidator().Validate(new RescheduleAppointmentCommand(Guid.Empty, Valid)).IsValid);
        Assert.False(new CancelBookedSessionValidator().Validate(new CancelBookedSessionCommand(Guid.NewGuid(), "broken", "Reason")).IsValid);
        Assert.False(new PreviewBookingValidator().Validate(new PreviewBookingQuery(Guid.NewGuid(), Guid.Empty)).IsValid);
    }
}
