using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

[Collection("Retreat progress")]
public sealed class RetreatProgressTests
{
    [Theory]
    [InlineData(-.03515625, .1171875)]
    [InlineData(0, 0)]
    [InlineData(.05, .1)]
    public void RetreatStartsBeyondNativeCompletionRadiusBesideACorner(double x, double y)
    {
        // Normal seed20261072: the actor at (184.96484375,45.1171875) repeatedly received (185,45),
        // already inside .15 native tolerance. This synthetic obstacle reproduces that A* seed.
        var map = Map(new(x, y));
        var state = State(map);
        var plan = new RetreatPlanner().Find(state, map);
        Assert.Equal("separation", plan.Status);
        Assert.NotNull(plan.Next);
        Assert.InRange(map.Actor.Position.DistanceTo(plan.Next), RetreatPlanner.MoveTolerance + 1e-9, 2.000000001);
        Assert.True(new SpatialCollisionField(map).SteeringRegionClear(map.Actor.Position, plan.Next,
            plan.Route!.UsesTightStartConnector ? 0 : .18));
        Assert.True(plan.Destination!.DistanceTo(state.Enemies[0].Position) >= map.Actor.Position.DistanceTo(state.Enemies[0].Position) + 2);
    }

    [Fact]
    public void RetreatKeepsAdvancingWhenNativeMovementStopsShortOfEachWaypoint()
    {
        var position = new MapPosition(-.03515625, .1171875);
        var initial = position;
        for (int i = 0; i < 8; i++)
        {
            var map = Map(position);
            var plan = new RetreatPlanner().Find(State(map), map);
            Assert.NotNull(plan.Next);
            double distance = position.DistanceTo(plan.Next);
            Assert.True(distance > RetreatPlanner.MoveTolerance);
            var after = new MapPosition(plan.Next.X + (position.X - plan.Next.X) * .12 / distance,
                plan.Next.Y + (position.Y - plan.Next.Y) * .12 / distance);
            Assert.True(position.DistanceTo(after) > .01);
            position = after;
        }
        Assert.True(position.DistanceTo(initial) > 4);
        Assert.True(position.DistanceTo(new(-4, 0)) > initial.DistanceTo(new(-4, 0)) + 3);
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(.51)]
    [InlineData(double.NaN)]
    public void InvalidFirstMovementDistanceIsRejected(double distance)
    {
        var field = new SpatialCollisionField(Map(new(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoutePlanner().Find(field, new(4, 0),
            minimumFirstMoveDistance: distance));
    }

    private static SafetyObservation State(SpatialSnapshot map) =>
        new(map.CollectedTick, map.Scope, true, "ai", false, map.Actor.Position, 50, new(true, 100, 15),
            [new("enemy", new(-4, 0))], null, MaxHealth: 250, LocalEnemiesComplete: true);

    private static SpatialSnapshot Map(MapPosition position) => SpatialPlannerTests.Map(
        [new("corner", "wall", new(.5, .5), new(new(.4, .4), new(.6, .6)), 0, "agent")])
        with { Actor = SpatialPlannerTests.Map([]).Actor with { Position = position } };
}

// These exercise the production planner's 20 ms deadlines. Concurrent test workers can exhaust
// that wall-clock budget while the planner thread is descheduled; isolate the geometry checks.
[CollectionDefinition("Retreat progress", DisableParallelization = true)]
public sealed class RetreatProgressCollection;
