using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StationaryThreatRoutingTests
{
    [Fact]
    public void RouteAndSteeringAvoidObservedAttackRangeEvenWhenTerrainIsClear()
    {
        var map = WithThreat(new(5, 0), 2);
        var field = new SpatialCollisionField(map);
        Assert.False(field.SegmentClear(new(0, 0), new(11, 0)));
        Assert.False(field.SteeringRegionClear(new(0, 5), new(10, -5)));
        var route = new RoutePlanner().Find(field, new(11, 0), timeBudget: TimeSpan.FromSeconds(3));
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Contains(route.Waypoints, p => Math.Abs(p.Y) >= 4);
        var previous = map.Actor.Position;
        foreach (var next in route.Waypoints)
        {
            Assert.True(field.SegmentClear(previous, next));
            previous = next;
        }
    }

    [Fact]
    public void InteractionCanStopOutsideAttackRangeWithoutReachingTheCorpseCenter()
    {
        var map = WithThreat(new(8, 0), 2);
        var field = new SpatialCollisionField(map);
        var corpse = new MapPosition(6, 0);
        var route = new RoutePlanner().Find(field, corpse, 3, timeBudget: TimeSpan.FromSeconds(3));
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.NotEmpty(route.Waypoints);
        Assert.InRange(route.Waypoints[^1].DistanceTo(corpse), .1, 3);
        Assert.True(route.Waypoints[^1].DistanceTo(new(8, 0)) > 4);
    }

    [Fact]
    public void ActorAlreadyExposedCanEscapeButCannotMoveCloserToTheThreat()
    {
        var field = new SpatialCollisionField(WithThreat(new(3, 0), 4));
        Assert.True(field.SegmentClear(new(0, 0), new(-8, 0)));
        Assert.False(field.SegmentClear(new(0, 0), new(1, 0)));
        Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(field, new(-8, 0)).Status);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(30, 99)]
    public void MalformedOrStaleThreatCannotBeAssumedHarmless(double range, long tick)
    {
        Assert.Throws<InvalidDataException>(() => WithThreat(new(5, 0), range, tick));
    }

    [Fact]
    public void TravelMarginSurvivesProjectedBuildingsWithoutChangingNativeAttackRange()
    {
        var map = WithThreat(new(5, 8), 2);
        Assert.True(new SpatialCollisionField(map).SegmentClear(new(0, 0), new(8, 0)));
        var field = new SpatialCollisionField(map, ExplorationPlanner.ThreatMargin);
        Assert.False(field.SegmentClear(new(0, 0), new(8, 0)));
        var projected = new SpatialEntity("planned", "chest", new(-8, -8), new(new(-8.4, -8.4), new(-7.6, -7.6)), 0, "agent");
        Assert.False(field.AppendEntities([projected]).SegmentClear(new(0, 0), new(8, 0)));
        Assert.Equal(2, map.StationaryThreats![0].Range);
    }

    [Fact]
    public void ActiveTravelIsCancelledWhenANewlyObservedWormMakesItsRemainingLegUnsafe()
    {
        var map = WithThreat(new(10, 30), 25);
        Assert.True(new SpatialCollisionField(map).SteeringRegionClear(new(0, 0), new(10, 0)));
        Assert.False(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(0, 0), [new(10, 0)], out _));
        Assert.True(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(0, 0), [new(-10, -10)], out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTravelMarginIsRejected(double margin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialCollisionField(WithThreat(new(5, 8), 2), margin));
    }

    private static SpatialSnapshot WithThreat(MapPosition position, double range, long tick = 100)
    {
        var node = JsonSerializer.SerializeToNode(SpatialPlannerTests.Map([]), Protocol.Json)!;
        node["stationaryThreats"] = JsonSerializer.SerializeToNode(new[]
        {
            new { id = "worm", position, range, collectedTick = tick }
        }, Protocol.Json);
        return SpatialSnapshot.Parse(new GameResponse(1, "threat", true, 100,
            JsonSerializer.SerializeToElement(node, Protocol.Json), null));
    }
}
