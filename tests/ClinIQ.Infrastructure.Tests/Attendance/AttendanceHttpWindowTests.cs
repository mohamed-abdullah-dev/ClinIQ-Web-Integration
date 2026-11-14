using ClinIQ.Infrastructure.Scheduling;
using ClinIQ.Infrastructure.Tests.Integration;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Attendance;

public sealed class AttendanceHttpWindowTests
{
    [Theory]
    [InlineData(4, 24, 1, 23, 59)]
    [InlineData(10, 29, 0, 22, 59)]
    [InlineData(11, 1, 0, 23, 59)]
    public void RealHttpFixtureUsesValidCairoEndpointsDuringBothDstTransitionsAndOrdinaryDays(int month, int day, int startHour, int endHour, int endMinute)
    {
        var clock = new CairoScheduleClock(TimeProvider.System); var window = AttendanceHttpWindow.ForDate(clock, new(2026, month, day));
        Assert.Equal(new TimeOnly(startHour, 0), window.StartsLocal); Assert.Equal(new TimeOnly(endHour, endMinute), window.EndsLocal);
        Assert.True(window.StartsAtUtc < window.EndsAtUtc);
    }
}
