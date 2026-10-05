using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StableArrivalTests
{
    [Fact]
    public void AWideApproachSearchesPastTheNearestBeltEndpoint()
    {
        var field = new SpatialCollisionField(Map());
        var destination = new MapPosition(.5, .5);
        var old = new RoutePlanner().Find(field, destination, 7.8);
        Assert.Equal(RouteStatus.Found, old.Status);
        Assert.False(PlacementPlanner.CanStop(field, old.Waypoints[^1]));

        var route = new RoutePlanner().Find(field, destination, 7.8, requireStableArrival: true);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.InRange(route.Waypoints[^1].DistanceTo(destination), 0, 7.8);
        Assert.True(PlacementPlanner.CanStop(field, route.Waypoints[^1]));
        AssertClear(field, route);
    }

    [Fact]
    public void StandingInsideTheRadiusOnABeltStillNeedsAStableArrival()
    {
        var map = Map();
        map = map with { Actor = map.Actor with { Position = new(7.5, .5) } };
        var field = new SpatialCollisionField(map);
        var route = new RoutePlanner().Find(field, new(.5, .5), 7.8, requireStableArrival: true);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.NotEmpty(route.Waypoints);
        Assert.True(PlacementPlanner.CanStop(field, route.Waypoints[^1]));
        AssertClear(field, route);
    }

    [Fact]
    public void AClearDirectDestinationOnABeltCannotBypassStableArrival()
    {
        var map = Map();
        map = map with { Entities = map.Entities.Where(e => e.Name != "chest").ToArray() };
        var field = new SpatialCollisionField(map);
        var destination = new MapPosition(.5, .5);
        Assert.True(field.SegmentClear(map.Actor.Position, destination));
        var route = new RoutePlanner().Find(field, destination, 2, requireStableArrival: true);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.NotEqual(destination, route.Waypoints[^1]);
        Assert.InRange(route.Waypoints[^1].DistanceTo(destination), 0, 2);
        Assert.True(PlacementPlanner.CanStop(field, route.Waypoints[^1]));
        AssertClear(field, route);
    }

    [Fact]
    public void AnArrivalAreaEntirelyOnBeltsIsRefusedWithoutAnUnstableFallback()
    {
        var field = new SpatialCollisionField(Map());
        var route = new RoutePlanner().Find(field, new(4.5, .5), .2,
            timeBudget: TimeSpan.FromSeconds(2), requireStableArrival: true);
        Assert.Equal(RouteStatus.NoRouteOnKnownGrid, route.Status);
        Assert.Empty(route.Waypoints);
    }

    [Fact]
    public void AnExactMoveWithoutTheStableArrivalRequirementRetainsItsDestination()
    {
        var field = new SpatialCollisionField(Map());
        var route = new RoutePlanner().Find(field, new(4.5, .5), .2);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Equal(new MapPosition(4.5, .5), route.Waypoints[^1]);
    }

    private static SpatialSnapshot Map()
    {
        var map = SpatialPlannerTests.Map([]);
        var belts = Enumerable.Range(-8, 21).Select(x => new SpatialEntity("belt-" + x, "belt", new(x + .5, .5),
            new(new(x + .1, .1), new(x + .9, .9)), 4, "agent"));
        return map with
        {
            Actor = map.Actor with { Position = new(10.5, .5) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["belt"] = new("belt", "transport-belt", new(new(-.4, -.4), new(.4, .4)),
                    new([], false, false, false), 1, 1)
            },
            Entities = [.. belts.Where(e => e.Position.X != .5),
                new("chest", "chest", new(.5, .5), new(new(.15, .15), new(.85, .85)), 0, "agent"),
                // A belt at the destination is walkable when the chest is removed in the direct-path test.
                new("destination-belt", "belt", new(.5, .5), new(new(.1, .1), new(.9, .9)), 4, "agent")]
        };
    }

    private static void AssertClear(SpatialCollisionField field, RoutePlan route)
    {
        var previous = field.Map.Actor.Position;
        foreach (var point in route.Waypoints)
        {
            Assert.True(field.SegmentClear(previous, point));
            previous = point;
        }
    }
}
