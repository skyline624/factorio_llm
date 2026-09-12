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
