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
    public void APartialRouteTowardAnUnobservedTargetStopsNearTheObservedEdgeNearerToIt()
    {
        var map = Map();
        var outside = new MapPosition(map.Bounds.Max.X + 100.5, .5);
        // Without the partial mode an unobserved target has no route, as before.
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot, new BeltRoutePlanner().Find(map, "belt", new(.5, .5), outside).Status);
        var route = new BeltRoutePlanner().Find(map, "belt", new(.5, .5), outside, partial: true);
        Assert.Equal(BeltRouteStatus.Partial, route.Status);
        var last = route.Belts[^1].Position;
        Assert.True(map.Bounds.Max.X - last.X < BeltRoutePlanner.EdgeMargin);
        Assert.True(Math.Abs(outside.X - last.X) <= Math.Abs(outside.X - .5) - BeltRoutePlanner.EdgeMargin);
        // A target in view is still reached exactly in partial mode.
        Assert.Equal(BeltRouteStatus.Found, new BeltRoutePlanner().Find(map, "belt", new(.5, .5), new(6.5, .5), partial: true).Status);
    }

    [Fact]
    public void InserterPointsAndBeltNeighbourhoodsAreForbiddenExceptAroundTheJoinedBelt()
    {
        var map = Map() with { Entities =
        [new("arm", "wall", new(2.5, 2.5), new(new(2.2, 2.2), new(2.8, 2.8)), 0, "own", PickupPosition: new(2.5, 1.7), DropPosition: new(2.5, 3.7)),
         new("line", "belt", new(6.5, .5), new(new(6.1, .1), new(6.9, .9)), 4, "own")] };
        var forbidden = BeltRoutePlanner.Forbidden(map);
        Assert.Contains(new MapPosition(2.5, 1.5), forbidden);
        Assert.Contains(new MapPosition(2.5, 3.5), forbidden);
        Assert.Contains(new MapPosition(7.5, 1.5), forbidden);
        Assert.DoesNotContain(new MapPosition(8.5, .5), forbidden);
        Assert.DoesNotContain(new MapPosition(7.5, .5), BeltRoutePlanner.Forbidden(map, joined: "line"));
        Assert.Equal(new MapPosition(7.5, .5), BeltRoutePlanner.Ahead(new(6.5, .5), 4));
    }

    [Fact]
    public void SearchBudgetExhaustionIsDistinctFromNoRoute()
    {
        Assert.Equal(BeltRouteStatus.BudgetExceeded, new BeltRoutePlanner().Find(Map(), "belt", new(.5, .5), new(6.5, .5), 1).Status);
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
