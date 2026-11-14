using ClinIQ.Domain.Scheduling;
using ClinIQ.Infrastructure.Scheduling;

namespace ClinIQ.Infrastructure.Tests.Integration;

internal static class AttendanceHttpWindow
{
    // A real-clock API test needs a valid in-day session; Cairo's DST boundary may reject midnight or 23:59.
    internal static SessionWindow ForDate(CairoScheduleClock clock, DateOnly date)
    {
        var valid = Enumerable.Range(0, 24 * 60).Select(m => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m))).Where(t =>
        {
            var local = date.ToDateTime(t, DateTimeKind.Unspecified);
            return !clock.Zone.IsInvalidTime(local) && !clock.Zone.IsAmbiguousTime(local);
        }).ToArray();
        return valid.Length < 2 ? throw new InvalidOperationException("No valid Cairo session window exists on this date.") :
            clock.Window(date, valid[0], valid[^1]).Value;
    }
}
