using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SharedInteractionApproachTests
{
    [Fact]
    public void WiringApproachKeepsBothLongArmAndChestWithinNativeReach()
    {
        var map = FactoryMaps.Grass(24);
        var arm = Entity(map, "arm", "inserter", new(8.5, .5));
        var chest = Entity(map, "chest", "iron-chest", new(10.5, .5));
        map = map with { Actor = map.Actor with { Position = new(.5, .5), ReachDistance = 10 }, Entities = [arm, chest] };
        var field = new SpatialCollisionField(map);
        var planner = new PlacementPlanner();
        var ordinary = planner.FindInteractionApproach(field, arm)!;
        Assert.True(ordinary.DistanceTo(chest.Position) > map.Actor.ReachDistance - 1);
        var shared = planner.FindInteractionApproach(field, [arm, chest]);
        Assert.NotNull(shared);
        Assert.All(new[] { arm, chest }, e => Assert.True(shared.DistanceTo(e.Position) <= map.Actor.ReachDistance - 1));
        Assert.True(PlacementPlanner.CanStop(field, shared));
        Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(field, shared).Status);
    }

    [Fact]
    public void TargetsWithoutCommonReachRefuseAnApproach()
    {
        var map = FactoryMaps.Grass(24);
        var targets = new[] { Entity(map, "left", "iron-chest", new(-10.5, .5)), Entity(map, "right", "iron-chest", new(10.5, .5)) };
        map = map with { Actor = map.Actor with { ReachDistance = 10 }, Entities = targets };
        Assert.Null(new PlacementPlanner().FindInteractionApproach(new(map), targets));
    }

    [Fact]
    public void SharedApproachRetainsAnAlreadySafeStationaryPosition()
    {
        var map = FactoryMaps.Grass(24);
        var targets = new[] { Entity(map, "arm", "inserter", new(3.5, .5)), Entity(map, "chest", "iron-chest", new(5.5, .5)) };
        map = map with { Actor = map.Actor with { Position = new(.5, .5), ReachDistance = 10 }, Entities = targets };
        Assert.Equal(map.Actor.Position, new PlacementPlanner().FindInteractionApproach(new(map), targets));
    }

    [Fact]
    public void SharedApproachChecksCancellationAndRejectsAnEmptyTargetList()
    {
        var field = new SpatialCollisionField(FactoryMaps.Grass(24));
        var planner = new PlacementPlanner();
        Assert.Throws<ArgumentException>(() => planner.FindInteractionApproach(field, Array.Empty<SpatialEntity>()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => planner.FindInteractionApproach(field,
            Array.Empty<SpatialEntity>(), cancelled.Token));
    }

    private static SpatialEntity Entity(SpatialSnapshot map, string id, string name, MapPosition position) =>
        new(id, name, position, map.Prototypes[name].CollisionBox.Translate(position), 0, "own");
}
