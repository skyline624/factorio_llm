using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ExplorationPlannerTests
{
    [Theory]
    [InlineData("transport-belt", false)]
    [InlineData("transport-belt", true)]
    [InlineData("underground-belt", false)]
    [InlineData("underground-belt", true)]
    [InlineData("splitter", false)]
    [InlineData("splitter", true)]
    public void ExplorationAndConstructionTravelStopBesideMovingTransport(string transportType, bool knownDestination)
    {
        var map = Map(new(0, 0));
        MapPosition? destination = knownDestination ? new(0, -60) : null;
        var catalog = Catalog();
        var preferred = new ExplorationPlanner().Choose(map, "", catalog, destination);
        var center = new MapPosition(preferred.X + .5, preferred.Y + .5);
        var bounds = new WorldBox(new(center.X - .4, center.Y - .4), new(center.X + .4, center.Y + .4));
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["moving-transport"] = new("moving-transport", transportType, new(new(-.4, -.4), new(.4, .4)),
                    new([], false, false, false), 1, 1)
            },
            Entities = [new("transport", "moving-transport", center, bounds, 0, "agent")]
        };
        var field = new SpatialCollisionField(map);
        Assert.True(field.Walkable(preferred));
        Assert.False(PlacementPlanner.CanStop(field, preferred));

        var selected = new ExplorationPlanner().Choose(map, "", catalog, destination);

        Assert.True(PlacementPlanner.CanStop(field, selected), $"The travel handoff at {selected} drifts on transport.");
        Assert.NotEqual(preferred, selected);
        Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(field, selected).Status);
    }

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
    public void AWaterfrontDoesNotTrapBlindExplorationInsideAlreadySurveyedGround()
    {
        // Historical local coverage extends across the shoreline. A northern frontier is
        // close in a straight line but cannot be approached through native water collisions.
        var surveyed = (from x in Enumerable.Range(-56, 122)
                        from y in Enumerable.Range(-99, 132)
                        select new SurveyedCell(x, y)).ToArray();
        var planner = new ExplorationPlanner();
        MapPosition actor = new(64, -348);
        var positions = new List<MapPosition>();
        for (int step = 0; step < 24; step++)
        {
            SpatialSnapshot map = Map(actor);
            map = map with { Rows = map.Rows.Select(r => r.Y < -352 ? r with { Name = "water" } : r).ToArray() };
            actor = planner.Choose(map, "", Catalog(), surveyed: surveyed, deaths: []);
            positions.Add(actor);
            Assert.True(new SpatialCollisionField(map).Walkable(actor));
        }
        Assert.Contains(positions, p => p.X < -180 || p.X > 212 || p.Y > 80);
    }

    [Fact]
    public void MoreThanSixteenUnapproachableFrontiersDoNotHideADryWayOutOfACorner()
    {
        // Synthetic coverage and a narrow passage reproduce the normal refusal:
        // many nearer northern frontiers cannot be approached, while dry ground leads south.
        var surveyed = (from x in Enumerable.Range(-56, 188)
                        from y in Enumerable.Range(-99, 132)
                        select new SurveyedCell(x, y)).ToArray();
        var planner = new ExplorationPlanner();
        MapPosition actor = new(220, -344);
        for (int step = 0; step < 32 && actor.X is >= -180 and <= 476 && actor.Y <= 80; step++)
        {
            SpatialSnapshot map = Map(actor);
            map = map with { Rows = map.Rows.SelectMany(r => Enumerable.Range(r.X, r.Length)
                .Select(x => new TileRun(x, r.Y, 1,
                    r.Y < -352 || r.Y < -300 && (x < 217 || x >= 224) ? "water" : "grass"))).ToArray() };
            actor = planner.Choose(map, "", Catalog(), surveyed: surveyed, deaths: []);
            Assert.True(new SpatialCollisionField(map).Walkable(actor));
        }
        Assert.True(actor.X < -180 || actor.X > 476 || actor.Y > 80, $"Exploration never left the covered corner: {actor}.");
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
