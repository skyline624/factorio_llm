using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PipeRoutePlannerTests
{
    [Fact]
    public void ConnectsActivePortsUsingCardinalPipeTilesAroundObstacles()
    {
        var map = Map();
        map = map with
        {
            Entities = [..map.Entities, new("block", "wall", new(2.5, .5),
            new(new(2, 0), new(3, 1)), 0, "agent")]
        };
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.Equal(new MapPosition(.5, .5), plan.Pipes[0]);
        Assert.Equal(new MapPosition(4.5, .5), plan.Pipes[^1]);
        Assert.DoesNotContain(new MapPosition(2.5, .5), plan.Pipes);
        for (int i = 1; i < plan.Pipes.Count; i++) Assert.Equal(1, plan.Pipes[i].DistanceTo(plan.Pipes[i - 1]), 6);
    }
    [Fact]
    public void DoesNotJoinAnUnrequestedNeighborFluidCircuit()
    {
        var map = Map();
        var foreign = new SpatialEntity("water", "pipe", new(2.5, -.5), new(new(2.2, -.8), new(2.8, -.2)), 0, "agent",
            FluidConnections: [new(1, 1, new(2.5, -.5), new(2.5, .5), Type: "normal", FlowDirection: "input-output", Filter: "water")]);
        map = map with { Entities = [.. map.Entities, foreign] };
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.DoesNotContain(new MapPosition(2.5, .5), plan.Pipes);
    }
    [Fact]
    public void RecipeFilteredPortCannotBeUsedForAnotherFluid()
    {
        Assert.Equal(PipeRouteStatus.InvalidEndpoints, new PipeRoutePlanner().Find(Map(), "pipe", "source", "target", "water").Status);
    }
    [Fact]
    public void ExhaustedSearchBudgetIsNotReportedAsImpossible()
    {
        Assert.Equal(PipeRouteStatus.BudgetExceeded, new PipeRoutePlanner().Find(Map(), "pipe", "source", "target", "oil", 1).Status);
    }
    [Fact]
    public void DirectNativePortAdjacencyNeedsNoInventedPipeTile()
    {
        var map = Map();
        var target = map.Entities.Single(e => e.Id == "target") with
        {
            Position = new(.5, .5),
            Bounds = new(new(0, 0), new(1, 1)),
            FluidConnections = [new(1, 1, new(.5, .5), new(-.5, .5), Type: "normal", FlowDirection: "input", Filter: "oil")]
        };
        map = map with { Entities = [map.Entities.Single(e => e.Id == "source"), target] };
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.Empty(plan.Pipes);
    }
    [Theory]
    [InlineData("oil", true)]
    [InlineData("water", false)]
    public void NativeNetworkVerificationRechecksTheTargetRecipeFilter(string targetFilter, bool expected)
    {
        var map = Map();
        var s = map.Entities.Single(e => e.Id == "source") with
        {
            FluidConnections = [new(1,1,new(-.5,.5),new(.5,.5),
            "target",1,"normal","output","oil")]
        };
        var t = map.Entities.Single(e => e.Id == "target") with
        {
            Position = new(.5, .5),
            Bounds = new(new(0, 0), new(1, 1)),
            FluidConnections = [new(1, 1, new(.5, .5), new(-.5, .5), "source", 1, "normal", "input", targetFilter)]
        };
        map = map with { Entities = [s, t] };
        Assert.Equal(expected, FluidNetwork.IsConnected(map, new("source", 1, 1, new(-.5, .5), new(.5, .5)),
            new("target", 1, 1, new(.5, .5), new(-.5, .5)), "oil"));
    }

    internal static SpatialSnapshot Map()
    {
        var m = SpatialPlannerTests.Map([]);
        var solid = m.Prototypes["wall"].Mask;
        var p = new Dictionary<string, EntityGeometry>(m.Prototypes)
        {
            ["pipe"] = new("pipe", "pipe", new(new(-.3, -.3), new(.3, .3)), solid, 1, 1,
                FluidBoxes: [new(1, "input-output", [])])
        };
        return m with
        {
            Actor = m.Actor with { Position = new(0, 5) },
            Prototypes = p,
            Items = new Dictionary<string, PlaceableItem> { { "pipe", new("pipe", 100) } },
            Entities =
            [new("source","wall",new(-.5,.5),new(new(-1,0),new(0,1)),0,"agent",
                FluidConnections:[new(1,1,new(-.5,.5),new(.5,.5),Type:"normal",FlowDirection:"output",Filter:"oil")]),
             new("target","wall",new(5.5,.5),new(new(5,0),new(6,1)),0,"agent",
                FluidConnections:[new(1,1,new(5.5,.5),new(4.5,.5),Type:"normal",FlowDirection:"input",Filter:"oil")])]
        };
    }
}
