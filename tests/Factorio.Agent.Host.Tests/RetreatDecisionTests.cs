using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RetreatDecisionTests
{
    [Theory]
    [InlineData(1, 180, false)] // A lone biter is fought.
    [InlineData(3, 180, true)]  // Outnumbered at 72% health: leave while it is still possible.
    [InlineData(3, 200, false)] // Outnumbered at 80%: keep shooting.
    [InlineData(1, 100, true)]  // Critical health always retreats.
    public void OutnumberedActorRetreatsBeforeCriticalHealth(int enemies, double health, bool retreat)
    {
        // Campaign 2026-10-01 (seed 20261002): a pack took the actor from above 100 to 68 health within one observation,
        // and it died on the way to the turret; retreating only at 40% leaves no margin against several biters.
        var state = new SafetyObservation(100, new("world", "session", "actor", 1, 1), true, "ai", false, new(0, 0), health,
            new WeaponState(true, 10, 18), Enumerable.Range(0, enemies).Select(i => new VisibleThreat($"biter-{i}", new(5 + i, 0))).ToArray(),
            null, MaxHealth: 250, LocalEnemiesComplete: true);
        Assert.Equal(retreat, RetreatPlanner.Needed(state));
    }
}
