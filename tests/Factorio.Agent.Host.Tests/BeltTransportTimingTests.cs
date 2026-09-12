using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportTimingTests
{
    [Fact]
    public void SlowScienceMachineGetsTimeToConsumeTheRequestedInputs()
    {
        Assert.True(BeltTransportTiming.ObservationBudget(25, 15, .03125, 1, 10) > 25 * 10 * 60);
    }

    [Fact]
    public void SlowUpstreamProductionAlsoExtendsTheWindow()
    {
        Assert.True(BeltTransportTiming.ObservationBudget(25, 15, .03125, 10, 1) > 25 * 10 * 60);
    }
}
