using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Scheduling;
using ClinIQ.Infrastructure.Tests.Integration;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Scheduling;

public sealed class CairoScheduleClockTests
{
    [Fact]
    public void DatedConversionUsesRuntimeCairoRulesInsteadOfAFixedOffset()
    {
        var cairo = new CairoScheduleClock(TimeProvider.System);
        var winter = cairo.Window(new(2026, 1, 15), new(18, 0), new(21, 0)).Value;
        var summer = cairo.Window(new(2026, 7, 15), new(18, 0), new(21, 0)).Value;
        Assert.NotEqual(winter.StartsLocal.ToTimeSpan() - winter.StartsAtUtc.TimeOfDay, summer.StartsLocal.ToTimeSpan() - summer.StartsAtUtc.TimeOfDay);
        foreach (var w in new[] { winter, summer }) Assert.Equal(w.Date.ToDateTime(w.StartsLocal), TimeZoneInfo.ConvertTime(w.StartsAtUtc, cairo.Zone).DateTime);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAndRepeatedLocalTimesAreRejectedExplicitly(bool ambiguous)
    {
        var cairo = new CairoScheduleClock(TimeProvider.System); var instant = FindTransition(cairo.Zone, ambiguous);
        var date = DateOnly.FromDateTime(instant); var start = TimeOnly.FromDateTime(instant);
        Assert.Equal(ScheduleErrors.TimeConversion, Assert.Single(cairo.Window(date, start, start.AddMinutes(1)).Errors));
    }
    [Fact]
    public void PublicationHorizonUsesCairoTodayIncludingItsMidnightBoundary()
    {
        var clock = new SqlServerIntegrationFixture.TestClock { UtcNow = new(2026, 10, 28, 22, 30, 0, TimeSpan.Zero) }; var cairo = new CairoScheduleClock(clock);
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, cairo.Zone).DateTime); Assert.Equal(expected, cairo.Today);
        Assert.True(cairo.InHorizon(expected, expected.AddDays(27))); Assert.False(cairo.InHorizon(expected, expected.AddDays(28))); Assert.False(cairo.InHorizon(expected.AddDays(-1), expected));
    }
    internal static DateTime FindTransition(TimeZoneInfo zone, bool ambiguous)
    {
        for (var date = new DateOnly(2026, 1, 1); date.Year == 2026; date = date.AddDays(1))
            for (var hour = 0; hour < 24; hour++)
            { var instant = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified); if (ambiguous ? zone.IsAmbiguousTime(instant) : zone.IsInvalidTime(instant)) return instant; }
        throw new InvalidOperationException("Runtime Cairo timezone data had no expected transition in the test year.");
    }
}
