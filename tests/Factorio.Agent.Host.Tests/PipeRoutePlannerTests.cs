using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PipeRoutePlannerTests
{
    [Fact]
    public void FirstFluidRouteLeavesRoomForTheOtherMachineInput()
    {
        var map = Map();
        var reserved = new MapPosition(2.5, 1.5);
        map = map with { Entities = map.Entities.Select(e => e.Id == "target" ? e with
            { FluidConnections = [.. e.FluidConnections!, new(2, 1, new(3.5, 1.5), reserved,
                Type: "normal", FlowDirection: "input", Filter: "water")] } : e).ToArray() };
        var route = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil");
        Assert.Equal(PipeRouteStatus.Found, route.Status);
        Assert.All(route.Pipes, p => Assert.True(Math.Abs(p.X - reserved.X) + Math.Abs(p.Y - reserved.Y) > 1));
    }

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

    [Fact]
    public void UndergroundHopsCrossAWallOrdinaryPipesCannotPass()
    {
        var map = Tunnels(Map());
        map = map with { Entities = [.. map.Entities, .. Enumerable.Range(-12, 25).Select(y =>
            new SpatialEntity($"wall-{y}", "wall", new(2.5, y + .5), new(new(2, y), new(3, y + 1)), 0, "agent"))] };
        Assert.Equal(PipeRouteStatus.NoRouteInSnapshot, new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil").Status);
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil", undergroundItem: "pipe-to-ground");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        var tunnels = plan.Pieces!.Where(p => p.Underground).ToArray();
        Assert.Equal(2, tunnels.Length);
        // The entrance opens back toward the source; the exit opens toward the target, both on the wall's row.
        Assert.True(tunnels[0].Position.X < 2.5 && tunnels[0].Direction == 12);
        Assert.True(tunnels[1].Position.X > 2.5 && tunnels[1].Direction == 4);
        Assert.Equal(plan.Pieces!.Where(p => !p.Underground).Select(p => p.Position), plan.Pipes);
    }

    [Fact]
    public void UndergroundHopsUseFewerEntitiesThanOrdinaryPipesOverDistance()
    {
        var map = Tunnels(Map());
        map = map with { Entities = map.Entities.Select(e => e.Id == "target" ? e with { Position = new(12.5, .5),
            Bounds = new(new(12, 0), new(13, 1)), FluidConnections = [new(1, 1, new(12.5, .5), new(11.5, .5),
                Type: "normal", FlowDirection: "input", Filter: "oil")] } : e).ToArray() };
        var ordinary = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil");
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil", undergroundItem: "pipe-to-ground");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.Equal(12, ordinary.Pipes.Count);
        Assert.True(plan.Pieces!.Count <= 4, $"{plan.Pieces!.Count} pieces");
        Assert.All(Hops(plan), hop => Assert.InRange(hop.Length, 1, 10));
    }

    [Fact]
    public void AnUndergroundEntranceNeverReusesATileOfItsOwnRoute()
    {
        // On seed 987654321 a relay placed a pipe, stepped aside and came back to the same tile for a tunnel entrance whose
        // open side had to face the aside step; the engine refused the second piece on the occupied tile.
        var map = Tunnels(Map());
        map = map with { Entities =
        [
            new("source", "wall", new(.5, -.5), new(new(0, -1), new(1, 0)), 0, "agent",
                FluidConnections: [new(1, 1, new(.5, -.5), new(.5, .5), Type: "normal", FlowDirection: "output", Filter: "oil")]),
            new("target", "wall", new(-6.5, .5), new(new(-7, 0), new(-6, 1)), 0, "agent",
                FluidConnections: [new(1, 1, new(-6.5, .5), new(-5.5, .5), Type: "normal", FlowDirection: "input", Filter: "oil")]),
            .. Enumerable.Range(-12, 25).Select(y => new SpatialEntity($"wall-{y}", "wall", new(-.5, y + .5), new(new(-1, y), new(0, y + 1)), 0, "agent"))
        ] };
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil", undergroundItem: "pipe-to-ground");
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.Equal(plan.Pieces!.Count, plan.Pieces.Select(p => p.Position).Distinct().Count());
    }

    [Fact]
    public void UndergroundHopNeverPassesAForeignTunnelOnItsAxis()
    {
        var map = Tunnels(Map());
        map = map with { Entities = [.. map.Entities, .. Enumerable.Range(-12, 25).Where(y => y != 0).Select(y =>
                new SpatialEntity($"wall-{y}", "wall", new(2.5, y + .5), new(new(2, y), new(3, y + 1)), 0, "agent")),
            new SpatialEntity("foreign", "pipe-to-ground", new(2.5, .5), new(new(2.2, .2), new(2.8, .8)), 4, "agent")] };
        var plan = new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil", undergroundItem: "pipe-to-ground");
        // The wall still leaves other rows to tunnel under, but never the foreign tunnel's own row.
        Assert.Equal(PipeRouteStatus.Found, plan.Status);
        Assert.DoesNotContain(Hops(plan), hop => Math.Abs(hop.Entrance.Position.Y - .5) < .01);
    }

    [Fact]
    public void NativeNetworkFollowsUndergroundPipeConnections()
    {
        var map = Map();
        var s = map.Entities.Single(e => e.Id == "source") with
            { FluidConnections = [new(1, 1, new(-.5, .5), new(.5, .5), "in", 1, "normal", "output", "oil")] };
        // Native 2.0.77 underground ports target the next tile under ground, not the partner's position.
        var entrance = new SpatialEntity("in", "pipe", new(.5, .5), new(new(.2, .2), new(.8, .8)), 12, "agent", FluidConnections:
            [new(1, 1, new(.5, .5), new(-.5, .5), "source", 1, "normal", "input-output"),
             new(1, 2, new(.5, .5), new(1.5, .5), "out", 1, "underground", "input-output")]);
        var exit = new SpatialEntity("out", "pipe", new(4.5, .5), new(new(4.2, .2), new(4.8, .8)), 4, "agent", FluidConnections:
            [new(1, 2, new(4.5, .5), new(3.5, .5), "in", 1, "underground", "input-output"),
             new(1, 1, new(4.5, .5), new(5.5, .5), "target", 1, "normal", "input-output")]);
        var t = map.Entities.Single(e => e.Id == "target") with
            { FluidConnections = [new(1, 1, new(5.5, .5), new(4.5, .5), "out", 1, "normal", "input", "oil")] };
        map = map with { Entities = [s, entrance, exit, t] };
        Assert.True(FluidNetwork.IsConnected(map, new("source", 1, 1, new(-.5, .5), new(.5, .5)),
            new("target", 1, 1, new(5.5, .5), new(4.5, .5)), "oil"));
    }

    private static IEnumerable<(PipePiece Entrance, int Length)> Hops(PipeRoutePlan plan)
    {
        var pieces = plan.Pieces!;
        for (int i = 0; i + 1 < pieces.Count; i++)
            if (pieces[i].Underground && pieces[i + 1].Underground && pieces[i].Direction != pieces[i + 1].Direction)
                yield return (pieces[i], (int)Math.Round(pieces[i].Position.DistanceTo(pieces[i + 1].Position)));
    }

    /// <summary>Native pipe-to-ground geometry: an open north port and an underground south port reaching ten tiles.</summary>
    internal static SpatialSnapshot Tunnels(SpatialSnapshot map)
    {
        MapPosition[] center = [new(0, 0), new(0, 0), new(0, 0), new(0, 0)];
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["pipe-to-ground"] = new("pipe-to-ground", "pipe-to-ground", new(new(-.3, -.3), new(.3, .3)), map.Prototypes["pipe"].Mask, 1, 1,
                FluidBoxes: [new(1, "input-output", [new(1, "normal", 0, "input-output", center, []),
                    new(2, "underground", 8, "input-output", center, [], MaxUndergroundDistance: 10)])])
        };
        return map with { Prototypes = prototypes, Items = new Dictionary<string, PlaceableItem>(map.Items)
            { ["pipe-to-ground"] = new("pipe-to-ground", 50) } };
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
