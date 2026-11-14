using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.Scheduling;

namespace ClinIQ.Infrastructure.Scheduling;

public sealed class CairoScheduleClock(TimeProvider clock)
{
    public const string ZoneId = "Africa/Cairo";
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);
    public bool InHorizon(DateOnly from, DateOnly until) => until >= from && from >= Today && until <= Today.AddDays(27);
    public Result<SessionWindow> Window(DateOnly date, TimeOnly start, TimeOnly end)
    {
        if (end <= start || start.Ticks % TimeSpan.TicksPerMinute != 0 || end.Ticks % TimeSpan.TicksPerMinute != 0) return ScheduleErrors.LocalTimes;
        var localStart = date.ToDateTime(start, DateTimeKind.Unspecified); var localEnd = date.ToDateTime(end, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(localStart) || Zone.IsInvalidTime(localEnd) || Zone.IsAmbiguousTime(localStart) || Zone.IsAmbiguousTime(localEnd)) return ScheduleErrors.TimeConversion;
        try
        {
            var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, Zone), TimeSpan.Zero);
            var utcEnd = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, Zone), TimeSpan.Zero);
            if (utcEnd <= utcStart) return ScheduleErrors.LocalTimes;
            return new SessionWindow(date, start, end, utcStart, utcEnd);
        }
        catch (ArgumentException) { return ScheduleErrors.TimeConversion; }
    }
}
