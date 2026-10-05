using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

// These route assertions share native wall-clock budgets with the other isolated retreat tests.
[Collection("Retreat progress")]
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

    [Theory]
    [InlineData(3, -20, true)]   // Outnumbered and healthy: reach the loaded turret before fighting.
    [InlineData(2, -20, false)]  // Two biters are fought where the actor stands.
    [InlineData(3, -3, false)]   // Already within the turret's inner coverage: fight beside it.
    public void OutnumberedActorSeeksObservedTurretCoverBeforeAFightItCannotWin(int enemies, double turretX, bool cover)
    {
        // Campaign 2026-10-01 (seed 20261002): the pistol-armed actor went from full health to death in about seven seconds
        // against packs; it retreated only once hurt and the biters outran it.
        var state = new SafetyObservation(100, new("world", "session", "actor", 1, 1), true, "ai", false, new(0, 0), 250,
            new WeaponState(true, 10, 15), Enumerable.Range(0, enemies).Select(i => new VisibleThreat($"biter-{i}", new(6 + i, 0))).ToArray(),
            null, MaxHealth: 250, Defenses: [new DefensiveRefuge("turret", new(turretX, 0), 18, 100, 100)], LocalEnemiesComplete: true);
        Assert.Equal(cover, RetreatPlanner.SeeksCover(state));
        Assert.False(RetreatPlanner.Needed(state));
        Assert.False(RetreatPlanner.SeeksCover(state with { Defenses = [] }));
    }

    [Fact]
    public void AHealthyActorGainsSeparationInsideLoadedCoverageBeforeThePackReachesIt()
    {
        var (map, state) = Covered();
        Assert.False(RetreatPlanner.Needed(state));
        Assert.False(RetreatPlanner.SeeksCover(state));
        Assert.True(RetreatPlanner.RepositionsInCover(state));
        var plan = new RetreatPlanner().Find(state, map);
        Assert.Equal("found", plan.Status);
        Assert.Equal("loaded", plan.RefugeId);
        Assert.True(plan.Next!.X < state.Position!.X);
        var refuge = Assert.Single(state.Defenses!);
        Assert.All(plan.Route!.Waypoints, p => Assert.True(p.DistanceTo(refuge.Position) <= RetreatPlanner.CoverRadius(refuge)));
        Assert.True(plan.Destination!.DistanceTo(state.Enemies[0].Position) >= state.Position.DistanceTo(state.Enemies[0].Position) + 2);
        Assert.InRange(plan.Next.DistanceTo(state.Position), RetreatPlanner.MoveTolerance, 2.01);
        Assert.Equal(250, state.Health);
    }

    [Theory]
    [InlineData(15, 15, true)]
    [InlineData(15, 15.01, false)]
    [InlineData(18, 18, true)]
    [InlineData(18, 18.01, false)]
    [InlineData(24, 24, true)]
    [InlineData(24, 24.01, false)]
    public void HealthyPreventiveCoverUsesTheEquippedNativeCombatRange(double range, double distance, bool needed)
    {
        var (map, state) = Covered();
        state = state with { Weapon = state.Weapon with { Range = range },
            Enemies = Enumerable.Range(0, 3).Select(i => new VisibleThreat($"unit-{i}", new(distance, 0), "unit")).ToArray() };
        Assert.Equal(needed, RetreatPlanner.RepositionsInCover(state));
        Assert.Equal(needed, RetreatPlanner.SeeksCover(state with { Position = new(0, 0),
            Defenses = [new("outside-cover", new(-12, 0), 18, 200, state.Tick)] }));
        if (!needed) Assert.Equal("not-needed", new RetreatPlanner().Find(state, map).Status);
    }

    [Theory]
    [InlineData(true, 50)]
    [InlineData(false, 250)]
    public void UrgentRetreatStillRespondsToDistantVisibleUnits(bool armed, double health)
    {
        var (_, state) = Covered();
        state = state with { Health = health, Weapon = state.Weapon with { Ready = armed },
            Enemies = Enumerable.Range(0, 3).Select(i => new VisibleThreat($"unit-{i}", new(25 + i, 0), "unit")).ToArray() };
        Assert.True(RetreatPlanner.Needed(state));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("dead")]
    [InlineData("stop-unconfirmed")]
    [InlineData("zero-health")]
    [InlineData("unknown-types")]
    [InlineData("static-enemies")]
    [InlineData("incomplete")]
    [InlineData("two-enemies")]
    [InlineData("no-cover")]
    [InlineData("outside-cover")]
    public void EarlyRepositionRequiresVisibleMobilePackAndCurrentLoadedCover(string invalid)
    {
        var (_, state) = Covered();
        state = invalid switch
        {
            "manual" => state with { ControlMode = "manual" }, "dead" => state with { Alive = false },
            "stop-unconfirmed" => state with { StopUnconfirmed = true }, "zero-health" => state with { Health = 0 },
            "unknown-types" => state with { Enemies = state.Enemies.Select(e => e with { Type = null }).ToArray() },
            "static-enemies" => state with { Enemies = state.Enemies.Select(e => e with { Type = "unit-spawner" }).ToArray() },
            "incomplete" => state with { LocalEnemiesComplete = false }, "two-enemies" => state with { Enemies = state.Enemies.Take(2).ToArray() },
            "no-cover" => state with { Defenses = [] }, "outside-cover" => state with { Position = new(-10, 0) }, _ => state
        };
        Assert.False(RetreatPlanner.RepositionsInCover(state));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("moved")]
    [InlineData("stale")]
    public void EarlyCoveredRepositionStillRequiresMatchingFreshTerrain(string changed)
    {
        var (map, state) = Covered();
        map = changed switch
        {
            "scope" => map with { Scope = map.Scope with { Generation = 4 } },
            "moved" => map with { Actor = map.Actor with { Position = new(-2, 0) } },
            _ => map with { CollectedTick = 161 }
        };
        if (changed == "scope") Assert.Throws<InvalidDataException>(() => new RetreatPlanner().Find(state, map));
        else Assert.Equal("observation-changed", new RetreatPlanner().Find(state, map).Status);
    }

    private static (SpatialSnapshot Map, SafetyObservation State) Covered()
    {
        var (map, _, state, _) = PortableDefenseTests.Fixture();
        var position = new MapPosition(2, 0);
        return (map with { Entities = [new("loaded", "turret-entity", position,
            new(new(1.3, -.7), new(2.7, .7)), 0, "agent")] },
            state with { Defenses = [new("loaded", position, 18, 200, 100)] });
    }
}
