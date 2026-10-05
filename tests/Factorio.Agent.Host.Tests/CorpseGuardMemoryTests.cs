using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CorpseGuardMemoryTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 2, 4);
    private static readonly Dictionary<string, MapPosition> Bodies = new() { ["guarded"] = new(50, 0), ["safe"] = new(-50, 0) };

    [Fact]
    public void AGuardLeavingTheCurrentViewDoesNotMakeItsDistantCorpseSafe()
    {
        var memory = Remember();
        var guarded = Review(Observe(new(-50, 0)), memory);
        Assert.Equal(["guarded"], guarded);
        Assert.DoesNotContain("safe", guarded);
    }

    [Fact]
    public void APartiallySeenZoneDoesNotClearMemoryButItsFullySeenBoundaryDoes()
    {
        var memory = Remember();
        Assert.Contains("guarded", Review(Observe(new(18, 0), radius: 63), memory));
        Assert.DoesNotContain("guarded", Review(Observe(new(18, 0), radius: 64), memory));
    }

    [Fact]
    public void AnEmptyTruncatedEnemyListDoesNotClearARememberedGuard()
    {
        var memory = Remember();
        Assert.Contains("guarded", Review(Observe(new(50, 0), truncated: true), memory));
    }

    [Fact]
    public void AGuardMovingJustOutsideTheZoneCannotClearItsUnseenFarSide()
    {
        var memory = Remember();
        // The unit is visible, now 33 tiles from the corpse, but the corpse's entire zone is not visible.
        Assert.Contains("guarded", Review(Observe(new(0, 0), [new(17, 0)]), memory));
    }

    [Fact]
    public void ACompleteClearZoneReleasesOnlyTheRememberedCorpseAndDisappearedBodiesAreForgotten()
    {
        var memory = Remember();
        Assert.Empty(Review(Observe(new(50, 0)), memory));
        memory.Add("vanished");
        Assert.Empty(Review(Observe(new(50, 0)), memory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherScopeOrAnOlderObservationCannotClearMemory(bool changedScope)
    {
        var memory = Remember();
        var response = Observe(new(50, 0), tick: changedScope ? 101 : 99,
            scope: changedScope ? Scope with { Generation = Scope.Generation + 1 } : Scope);
        Assert.Throws<InvalidDataException>(() => Review(response, memory));
        Assert.Contains("guarded", memory);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void AnInvalidObservationRadiusCannotClearMemory(int radius)
    {
        var memory = Remember();
        Assert.Throws<InvalidDataException>(() => Review(Observe(new(50, 0), radius: radius), memory));
        Assert.Contains("guarded", memory);
    }

    private static HashSet<string> Remember()
    {
        var memory = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(["guarded"], Review(Observe(new(0, 0), [new(55, 0)]), memory));
        return memory;
    }

    private static IReadOnlySet<string> Review(GameResponse response, HashSet<string> memory) =>
        CorpseRecoveryController.GuardedCorpses(response, Bodies, memory, Scope, 100);

    private static GameResponse Observe(MapPosition actor, MapPosition[]? enemies = null, bool truncated = false, int radius = 64,
        long tick = 100, ActorScope? scope = null) => new(1, "observe", true, tick, Protocol.ToElement(new
        {
            scope = scope ?? Scope, collectedTick = tick,
            coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick, radius, enemiesTruncated = truncated,
                enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
            agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = actor, health = 250,
                weapon = new { ready = false, rounds = 0, range = 0 } },
            enemies = (enemies ?? []).Select((position, index) => new { id = $"enemy-{index}", type = "unit", position, collectedTick = tick })
        }));
}
