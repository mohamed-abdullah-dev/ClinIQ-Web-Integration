using ClinIQ.Domain.Scheduling;
using Xunit;

namespace ClinIQ.Domain.Tests.Scheduling;

public sealed class BookingSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(60, 19)]
    [InlineData(30, 19.5)]
    [InlineData(15, 19.75)]
    [InlineData(0, 20)]
    public void CutoffIsCalculatedFromEndAndClosesAtTheExactDeadline(int cutoff, double utcHour)
    {
        var row = Session(cutoff); Assert.Equal(Now.UtcDateTime.Date.AddHours(utcHour), row.BookingClosesAtUtc.UtcDateTime);
        Assert.True(row.CanAllocate(row.BookingClosesAtUtc.AddTicks(-1), true)); Assert.False(row.CanAllocate(row.BookingClosesAtUtc, true));
    }
    [Fact]
    public void ALongOffsetExplicitlyClosesBeforeTheSessionStarts()
    { var row = Session(200); Assert.True(row.BookingClosesAtUtc < row.StartsAtUtc); Assert.False(row.CanAllocate(row.StartsAtUtc, true)); }
    [Fact]
    public void CapacityCountsAllocatedWorkAndNeverAutomaticallyResumesAPause()
    {
        var row = Session(); Assert.True(row.Allocate(Now, true).IsSuccess); Assert.True(row.Allocate(Now, true).IsSuccess);
        Assert.True(row.ChangeCapacity(1, Now).IsError); Assert.True(row.ChangeCapacity(2, Now).IsSuccess);
        Assert.True(row.Pause(true, true, Now).IsSuccess); Assert.True(row.ChangeCapacity(5, Now).IsSuccess); Assert.True(row.BookingPaused);
        Assert.True(row.Cancel(Guid.NewGuid(), "Cannot silently cancel allocated bookings", Now).IsError);
    }
    [Fact]
    public void CutoffReopeningDoesNotOverrideManualPauseOrEndedState()
    {
        var row = Session(60); var during = row.EndsAtUtc.AddMinutes(-30);
        Assert.False(row.CanAllocate(during, true)); Assert.True(row.ChangeCutoff(0, during).IsSuccess); Assert.True(row.CanAllocate(during, true));
        row.Pause(true, true, during); row.ChangeCutoff(15, during); Assert.True(row.BookingPaused); Assert.False(row.CanAllocate(during, true));
        Assert.True(row.Pause(false, false, during).IsError); Assert.True(row.Pause(false, true, row.EndsAtUtc).IsError); Assert.True(row.ChangeCutoff(0, row.EndsAtUtc).IsError);
    }
    [Fact]
    public void TimesCannotMoveAfterStartOrAfterAllocation()
    {
        var row = Session(); var window = new SessionWindow(row.LocalDate, new(20, 0), new(22, 0), row.StartsAtUtc.AddHours(1), row.EndsAtUtc.AddHours(1));
        Assert.True(row.ChangeTimes(window, row.StartsAtUtc).IsError); row.Allocate(Now, true); Assert.True(row.ChangeTimes(window, Now).IsError);
    }
    [Fact]
    public void CancellationPreservesIdentityAndPreventsReopening()
    {
        var row = Session(); var id = row.Id; Assert.True(row.Cancel(Guid.NewGuid(), "Doctor unavailable", Now).IsSuccess);
        Assert.Equal(id, row.Id); Assert.Equal("Doctor unavailable", row.CancellationReason); Assert.False(row.CanAllocate(Now, true));
        Assert.True(row.ChangeCapacity(20, Now).IsError); Assert.True(row.Pause(false, true, Now).IsError);
    }
    [Fact]
    public void NegativeOrUnrepresentableCutoffNeverThrowsOrChangesThePeriod()
    {
        var row = Session(); var closes = row.BookingClosesAtUtc;
        Assert.True(row.ChangeCutoff(-1, Now).IsError); Assert.True(row.ChangeCutoff(int.MaxValue, Now).IsError); Assert.Equal(closes, row.BookingClosesAtUtc);
    }
    private static BookingSession Session(int cutoff = 0) => BookingSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(new(2026, 10, 28), new(21, 0), new(23, 0), new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero)), 10, cutoff, Now).Value;
}
public sealed class ScheduleRuleTests
{
    [Fact]
    public void AdjacentPeriodsDoNotOverlapButCrossLocationOverlapsDo()
    {
        var first = Rule(new(18, 0), new(20, 0)); Assert.False(first.Overlaps(Rule(new(20, 0), new(22, 0)))); Assert.True(first.Overlaps(Rule(new(19, 0), new(21, 0))));
        Assert.False(first.Overlaps(Rule(new(19, 0), new(21, 0)) with { IsActive = false }));
    }
    [Fact]
    public void EffectiveRangeIntersectionNeedsAnActualMatchingWeekday()
    {
        var a = Rule(new(18, 0), new(20, 0)) with { EffectiveFrom = new(2026, 10, 26), EffectiveUntil = new(2026, 11, 1) };
        var b = Rule(new(18, 0), new(20, 0)) with { EffectiveFrom = new(2026, 10, 27), EffectiveUntil = new(2026, 11, 2) }; Assert.False(a.Overlaps(b));
    }
    [Theory]
    [InlineData(20, 18)]
    [InlineData(18, 18)]
    public void OvernightOrZeroDurationRulesAreRejected(int start, int end) => Assert.True(Rule(new(start, 0), new(end, 0)).Validate().IsError);
    private static ScheduleRuleData Rule(TimeOnly start, TimeOnly end) => new(DayOfWeek.Monday, start, end, 10, 0, new(2026, 10, 1), null);
}
