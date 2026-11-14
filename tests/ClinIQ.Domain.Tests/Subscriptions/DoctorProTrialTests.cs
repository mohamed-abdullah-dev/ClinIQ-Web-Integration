using ClinIQ.Domain.Subscriptions;
using Xunit;

namespace ClinIQ.Domain.Tests.Subscriptions;

public sealed class DoctorProTrialTests
{
    [Fact]
    public void ProUsesThirtyElapsedDaysAndExpiresAtTheExactBoundary()
    {
        var activation = new DateTimeOffset(2026, 10, 10, 18, 0, 0, TimeSpan.FromHours(3));
        var trial = DoctorProTrial.Activate(Guid.NewGuid(), activation);

        Assert.Equal(TimeSpan.Zero, trial.ActivatedAtUtc.Offset);
        Assert.Equal(TimeSpan.FromHours(720), trial.ExpiresAtUtc - activation);
        Assert.Equal(DoctorPlan.Standard, trial.EffectivePlanAt(activation.AddTicks(-1)));
        Assert.Equal(DoctorPlan.Pro, trial.EffectivePlanAt(activation));
        Assert.Equal(DoctorPlan.Pro, trial.EffectivePlanAt(trial.ExpiresAtUtc.AddTicks(-1)));
        Assert.Equal(DoctorPlan.Standard, trial.EffectivePlanAt(trial.ExpiresAtUtc));
    }
}
