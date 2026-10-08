using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TransportLinkAttemptsTests
{
    [Fact]
    public void ATriedPairWaitsAGameHourWhileOtherPairsStayOpen()
    {
        // Seed 20261106 on 2026-10-08: every between-goal round spent its budget surveying the same far corridor for eight hours.
        var state = TransportLinkAttempts.With(new TransportLinkAttemptState(1, "world", []), new("smelter", "circuits", "iron-plate", 1000));
        Assert.True(TransportLinkAttempts.Delayed(state, "smelter", "circuits", "iron-plate", 1000 + TransportLinkAttempts.RetryTicks - 1));
        Assert.False(TransportLinkAttempts.Delayed(state, "smelter", "circuits", "iron-plate", 1000 + TransportLinkAttempts.RetryTicks));
        Assert.False(TransportLinkAttempts.Delayed(state, "smelter", "gears", "iron-plate", 1001));
        Assert.False(TransportLinkAttempts.Delayed(state, "smelter", "circuits", "copper-plate", 1001));
        // A new attempt on the same pair replaces the old one.
        state = TransportLinkAttempts.With(state, new("smelter", "circuits", "iron-plate", 5000));
        Assert.Equal([5000L], state.Attempts.Select(a => a.Tick));
    }
}
