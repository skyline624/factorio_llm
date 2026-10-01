using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ExplorationPlannerTests
{
    [Fact]
    public void SeeingAFrontierDoesNotReverseTheWalkBeforeApproachingIt()
    {
        var planner = new ExplorationPlanner();
        SpatialSnapshot start = Map(new(0, 0));
        var catalog = new ProductionCatalog(start.Scope, 1, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        MapPosition first = planner.Choose(start, "", catalog);
        Assert.Equal(new MapPosition(0, -28), first);
        MapPosition next = planner.Choose(Map(first), "", catalog);
        Assert.True(next.Y <= -44, $"The observed frontier is north; the next waypoint {next} should keep approaching it.");
    }

    [Fact]
    public void ReachedFrontierContinuesTowardNearbyUnknownSpaceInsteadOfTheOrigin()
    {
        var planner = new ExplorationPlanner();
        SpatialSnapshot start = Map(new(0, 0));
        var catalog = new ProductionCatalog(start.Scope, 1, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        MapPosition first = planner.Choose(start, "", catalog);
        MapPosition second = planner.Choose(Map(first), "", catalog);
        MapPosition third = planner.Choose(Map(second), "", catalog);
        Assert.True(third.Y <= -48, $"After approaching the northern frontier at {second}, {third} returns across already observed terrain.");
    }

    [Fact]
    public void FractionalArrivalDoesNotMakeExplorationDriftIntoOnlyPositiveCoordinates()
    {
        var planner = new ExplorationPlanner();
        SpatialSnapshot start = Map(new(0, 0));
        var catalog = new ProductionCatalog(start.Scope, 1, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        MapPosition position = start.Actor.Position;
        var visited = new List<MapPosition>();
        for (int step = 0; step < 32; step++)
        {
            MapPosition next = planner.Choose(Map(position), "", catalog);
            visited.Add(next);
            position = new(next.X - 0.05, next.Y - 0.05);
        }
        Assert.Contains(visited, p => p.X < -48);
        Assert.Contains(visited, p => p.Y < -48);
        Assert.Contains(visited, p => p.X > 48);
        Assert.Contains(visited, p => p.Y > 48);
    }

    [Fact]
    public void FrontierInsideARecentDeathZoneIsNotChosen()
    {
        // Without the zone the first step heads north (see the first test above).
        var death = new NativeDeathTransition(1, 100, 17, 1, new(0, -52));
        MapPosition first = new ExplorationPlanner().Choose(Map(new(0, 0)), "", Catalog(), deaths: [death]);
        Assert.False(DangerZones.Covers(death, first), $"{first} lies in the death zone.");
        Assert.True(first.X <= -16, $"The next safe frontier is west; {first} does not head there.");
    }

    [Fact]
    public void FrontierJustOutsideADeathZoneLosesToASaferOne()
    {
        // The northern frontier at (0,-52) stays 6 tiles outside this zone: allowed, but no longer preferred.
        var death = new NativeDeathTransition(1, 100, 17, 1, new(0, -90));
        MapPosition first = new ExplorationPlanner().Choose(Map(new(0, 0)), "", Catalog(), deaths: [death]);
        Assert.True(first.X <= -16, $"{first} still heads toward the frontier beside the zone.");
    }

    [Fact]
    public void ActorStandingInADeathZoneMayStillLeaveIt()
    {
        // Every point within 28 tiles lies in this zone; only steps away from its centre remain allowed.
        var death = new NativeDeathTransition(1, 100, 17, 1, new(4, 0));
        MapPosition first = new ExplorationPlanner().Choose(Map(new(0, 0)), "", Catalog(), deaths: [death]);
        Assert.True(first.DistanceTo(death.Position) > 4, $"{first} moves deeper into the zone.");
    }

    [Fact]
    public void EveryFrontierNearAKnownWormIsReportedAsDangerNotAsBlocked()
    {
        SpatialSnapshot start = Map(new(0, 0));
        start = start with { StationaryThreats = [new("worm", new(0, 10), 64, start.CollectedTick)] };
        var error = Assert.Throws<ExplorationDangerException>(() => new ExplorationPlanner().Choose(start, "", Catalog(), deaths: []));
        Assert.Contains("does not prove", error.Message);
        // A caller's own destination remains its choice: only the routing margin of the worm applies.
        Assert.Equal(new MapPosition(0, -28), new ExplorationPlanner().Choose(start, "", Catalog(), new(0, -60)));
    }

    private static ProductionCatalog Catalog() => new(Map(new(0, 0)).Scope, 1, [], new Dictionary<string, NativeItem>(),
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());

    private static SpatialSnapshot Map(MapPosition actor)
    {
        SpatialSnapshot map = SpatialPlannerTests.Map([]);
        int x = (int)Math.Floor(actor.X) - 48, y = (int)Math.Floor(actor.Y) - 48;
        return map with { Actor = map.Actor with { Position = actor }, Bounds = new(new(x, y), new(x + 97, y + 97)),
            Rows = Enumerable.Range(y, 97).Select(row => new TileRun(x, row, 97, "grass")).ToArray(),
            Coverage = map.Coverage with { Radius = 48 } };
    }
}
