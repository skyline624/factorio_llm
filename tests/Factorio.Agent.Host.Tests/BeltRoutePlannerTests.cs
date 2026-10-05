using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltRoutePlannerTests
{
    [Fact]
    public void RoutesDirectedBeltsAroundAnObstacleWithoutJoiningAnExistingLine()
    {
        var map = Map();
        map = map with { Entities =
        [new("wall", "wall", new(2.5, .5), new(new(2, -1), new(3, 2)), 0, "own"),
         new("other", "belt", new(4.5, 1.5), new(new(4.1, 1.1), new(4.9, 1.9)), 0, "own")] };
        var route = new BeltRoutePlanner().Find(map, "belt", new(.5, .5), new(6.5, .5));
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.Equal(new MapPosition(.5, .5), route.Belts[0].Position);
        Assert.Equal(new MapPosition(6.5, .5), route.Belts[^1].Position);
        for (int i = 0; i < route.Belts.Count - 1; i++)
        {
            var step = ExtractionPlanner.Rotate(new(0, -1), route.Belts[i].Direction);
            Assert.Equal(route.Belts[i + 1].Position, new MapPosition(route.Belts[i].Position.X + step.X, route.Belts[i].Position.Y + step.Y));
        }
        Assert.All(route.Belts, b => Assert.True(Math.Abs(b.Position.X - 4.5) + Math.Abs(b.Position.Y - 1.5) > 1));
    }

    [Fact]
    public void SearchBudgetExhaustionIsDistinctFromNoRoute()
    {
        Assert.Equal(BeltRouteStatus.BudgetExceeded, new BeltRoutePlanner().Find(Map(), "belt", new(.5, .5), new(6.5, .5), 1).Status);
    }

    [Theory]
    [InlineData(4.5, 1.5, false, false, false)]
    [InlineData(4.5, 1.5, true, false, true)]
    [InlineData(4.5, .5, true, false, false)]
    [InlineData(4.5, 1.5, false, true, true)]
    [InlineData(3.5, .5, true, true, false)]
    public void FlowIndexPreservesRetainedLineFrontAndNativeCollisionRules(double x, double y,
        bool retained, bool inlet, bool allowed)
    {
        var map = Map();
        var belt = map.Prototypes["belt"];
        var position = new MapPosition(3.5, .5);
        map = map with { Entities = [new("old-line", "belt", position, belt.CollisionBox.Translate(position), 4, "own")] };
        var point = new MapPosition(x, y);
        var route = new BeltRoutePlanner().Find(map, "belt", point, point,
            inletBeltId: inlet ? "old-line" : null, existingBusBelts: retained ? new HashSet<string> { "old-line" } : null);
        Assert.Equal(allowed ? BeltRouteStatus.Found : BeltRouteStatus.NoRouteInSnapshot, route.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongOpenRoutesFitTheirNodeBudgetWithoutExploringEveryEqualCostTile(bool reverse)
    {
        var map = Map();
        map = map with
        {
            Bounds = new(new(-64, -64), new(65, 65)),
            Rows = Enumerable.Range(-64, 129).Select(y => new TileRun(-64, y, 129, map.Rows[0].Name)).ToArray(),
            Entities = []
        };
        MapPosition start = new(-58.5, -10.5), target = new(55.5, 42.5);
        if (reverse) (start, target) = (target, start);
        var route = new BeltRoutePlanner().Find(map, "belt", start, target, nodeBudget: 200);
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.Equal(168, route.Belts.Count);
        Assert.InRange(route.ExpandedNodes, 1, 200);
        var field = new SpatialCollisionField(map);
        Assert.All(route.Belts, b => Assert.True(field.PlacementClear(map.Prototypes["belt"], b.Position, b.Direction)));
    }

    [Fact]
    public void UnrelatedResourceEntitiesDoNotHideAnInsertersReservedDropTile()
    {
        var map = Map();
        var resource = map.Prototypes["wall"] with
            { Name = "resource", Type = "resource", Mask = new CollisionMask([], false, false, false) };
        var arm = map.Prototypes["wall"] with { Name = "arm", Type = "inserter" };
        MapPosition drop = new(3.5, .5);
        var position = new MapPosition(3.5, -3.5);
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["resource"] = resource, ["arm"] = arm },
            Entities = [.. Enumerable.Range(0, 200).Select(i => new SpatialEntity($"ore-{i}", "resource",
                new(-11.5 + i % 20, -11.5 + i / 20), resource.CollisionBox.Translate(new(-11.5 + i % 20, -11.5 + i / 20)), 0, "neutral")),
                new("arm", "arm", position, arm.CollisionBox.Translate(position), 0, "own", DropPosition: drop)]
        };
        var route = new BeltRoutePlanner().Find(map, "belt", new(.5, .5), new(6.5, .5));
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.DoesNotContain(route.Belts, b => b.Position == drop);
    }

    internal static SpatialSnapshot Map()
    {
        var map = SpatialPlannerTests.Map([]);
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["belt"] = new("belt", "transport-belt", new(new(-.4, -.4), new(.4, .4)), map.Prototypes["wall"].Mask, 1, 1, BeltSpeed: .03125) },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["belt"] = new("belt", 100) }
        };
    }
}
