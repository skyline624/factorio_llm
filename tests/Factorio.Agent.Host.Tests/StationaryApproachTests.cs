using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StationaryApproachTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApproachesAvoidBeltMotionWithoutBlockingWalkingAcrossBelts(bool construction)
    {
        var map = SpatialPlannerTests.Map([]);
        var belt = new SpatialEntity("belt", "belt", new(.5, .5), new(new(.1, .1), new(.9, .9)), 4, "agent");
        var target = new SpatialEntity("target", "chest", new(5.5, .5), new(new(5.15, .15), new(5.85, .85)), 0, "agent");
        map = map with
        {
            Actor = map.Actor with { Position = new(.5, .5) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["belt"] = new("belt", "transport-belt", new(new(-.4, -.4), new(.4, .4)), new([], false, false, false), 1, 1) },
            Entities = [belt, target]
        };
        var field = new SpatialCollisionField(map);
        Assert.True(field.Walkable(belt.Position));
        var approach = construction
            ? new PlacementPlanner().FindApproach(field, "chest", new(new(8.5, .5), 0, 0))
            : new PlacementPlanner().FindInteractionApproach(field, target);
        Assert.NotNull(approach);
        Assert.False(new WorldBox(new(0, 0), new(1, 1)).Overlaps(field.Character.CollisionBox.Translate(approach)));
        Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(field, approach).Status);
    }
}
