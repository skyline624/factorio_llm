using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidCellPlannerTests
{
    private static readonly CellEquipment Chemical = new("chemical-plant", "inserter", "iron-chest", "small-electric-pole");
    private static readonly CellEquipment Refinery = Chemical with { Machine = "oil-refinery" };
    private static readonly CellEquipment Pumpjack = Chemical with { Machine = "pumpjack" };

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void ChestFedInsertersStandOnASideWithoutFluidPorts(int direction)
    {
        var map = OilMaps.Map([]);
        var placement = new PlacementCandidate(new(.5, .5), direction, 0);
        var layouts = new FluidCellPlanner().Layouts(map, Chemical, placement, true, true).ToArray();
        // entities.lua: the chemical plant pipes enter from one face and leave from the opposite one; the two other faces stay free.
        Assert.Equal(2, layouts.Length);
        var targets = FluidCellPlanner.Ports(map.Prototypes["chemical-plant"], placement).Select(p => p.TargetPosition).ToArray();
        foreach (var layout in layouts)
        {
            Assert.Equal(["input-chest", "input-inserter", "machine", "output-chest", "output-inserter", "pole"], layout.Entities.Select(e => e.Role).Order());
            Assert.All(layout.Entities.Where(e => e.Role != "machine"), e => Assert.DoesNotContain(targets, t => t.DistanceTo(e.Position) < .01));
            var body = map.Prototypes["chemical-plant"].CollisionBox.Rotate(direction).Translate(placement.Position);
            var arm = map.Prototypes["inserter"];
            var input = layout.Role("input-inserter")!;
            var output = layout.Role("output-inserter")!;
            Assert.True(body.Contains(At(input, arm.InserterDrop!)));
            Assert.Equal(Tile(layout.Role("input-chest")!.Position), Tile(At(input, arm.InserterPickup!)));
            Assert.True(body.Contains(At(output, arm.InserterPickup!)));
            Assert.Equal(Tile(layout.Role("output-chest")!.Position), Tile(At(output, arm.InserterDrop!)));
            var pole = map.Prototypes["small-electric-pole"];
            Assert.All(layout.Entities.Where(e => e.Role is "machine" or "input-inserter" or "output-inserter"), e =>
                Assert.True(PowerGridPlanner.Supplies(layout.Role("pole")!.Position, pole, Box(map, e))));
            Assert.True(layout.Footprint.Contains(body));
        }
    }

    [Fact]
    public void AMachineWithoutSolidsOnlyGetsAPoleOffItsPortTargets()
    {
        var map = OilMaps.Map([]);
        foreach (int direction in new[] { 0, 4, 8, 12 })
        {
            var placement = new PlacementCandidate(new(.5, .5), direction, 0);
            var targets = FluidCellPlanner.Ports(map.Prototypes["pumpjack"], placement).Select(p => p.TargetPosition).ToArray();
            var layouts = new FluidCellPlanner().Layouts(map, Pumpjack, placement, false, false).ToArray();
            // mining-drill.lua: the pumpjack exposes one output port, so three faces can carry the pole.
            Assert.Equal(3, layouts.Length);
            Assert.All(layouts, l => Assert.Equal(["machine", "pole"], l.Entities.Select(e => e.Role).Order()));
            Assert.All(layouts, l => Assert.DoesNotContain(targets, t => t.DistanceTo(l.Role("pole")!.Position) < .01));
        }
    }

    [Fact]
    public void SitesAreAcceptedOnlyWhenEveryCompatibleInputBoxCanBeRouted()
    {
        var (map, stock) = OilMaps.WithGasSource([]);
        var site = new FluidCellPlanner().Find(map, "agent", Chemical, "pipe", new(-10, 0), true, true, ["petroleum-gas"],
            (current, fluid) => new FluidSupplyPlanner().Find(current, stock, "pipe", FluidCellPlanner.PlannedId, fluid))!;
        var projected = FluidCellPlanner.Project(map, "agent", site.Layout, Chemical);
        foreach (int box in new[] { 1, 2 })
        {
            // The recipe picks the gas box only after construction; both must be reachable beforehand.
            var only = FluidCellPlanner.Assign(projected, new Dictionary<int, string> { [box] = "petroleum-gas" });
            Assert.Equal(PipeRouteStatus.Found, new PipeRoutePlanner().Find(only, "pipe", "refinery", FluidCellPlanner.PlannedId, "petroleum-gas").Status);
        }
        var placed = site.Layout.Entities.Select(e => Box(map, e)).ToArray();
        Assert.All(site.Supplies.SelectMany(s => s.Supply.Route.Pipes), p => Assert.DoesNotContain(placed, b => b.Contains(p)));
    }

    [Fact]
    public void ABlockedSecondInputBoxRejectsThePlacement()
    {
        var (map, stock) = OilMaps.WithGasSource([]);
        var layout = new FluidCellPlanner().Layouts(map, Chemical, new(new(-6.5, 6.5), 0, 0), true, true).First();
        var projected = FluidCellPlanner.Project(map, "agent", layout, Chemical);
        FluidSupplyRoute? Route(SpatialSnapshot current, string fluid) =>
            new FluidSupplyPlanner().Find(current, stock, "pipe", FluidCellPlanner.PlannedId, fluid);
        Assert.NotNull(FluidCellPlanner.RouteEveryAssignment(projected, "pipe", ["petroleum-gas"], Route));
        // A wall on the tile in front of the second input port (-5.5, 4.5) leaves only one of the two recipe choices routable.
        var walled = projected with { Entities = [.. projected.Entities, OilMaps.Wall("wall", new(-5.5, 4.5))] };
        Assert.Null(FluidCellPlanner.RouteEveryAssignment(walled, "pipe", ["petroleum-gas"], Route));
    }

    [Fact]
    public void AmongTheNearestRoutableSitesTheShortestPipeRouteWins()
    {
        // Live fixture of 30/09: the nearest refinery site east of a north-facing pumpjack needed sixteen pipes around it.
        var deposit = OilMaps.Crude("crude", new(12.5, 6.5), 600000);
        var jack = new SpatialEntity("jack", "pumpjack", deposit.Position, new WorldBox(new(-1.2, -1.2), new(1.2, 1.2)).Translate(deposit.Position),
            0, "agent", FluidConnections: [new(1, 1, new(13.5, 5.5), new(13.5, 4.5), Type: "normal", FlowDirection: "output", Filter: "crude-oil")]);
        var map = OilMaps.Map([deposit, jack]);
        FluidSupplyRoute? Route(SpatialSnapshot current, string fluid) =>
            new PipeRoutePlanner().Find(current, "pipe", "jack", FluidCellPlanner.PlannedId, fluid) is { Status: PipeRouteStatus.Found } route ? new("jack", route) : null;
        int Pipes(FluidCellSite site) => site.Supplies.Sum(s => s.Supply.Route.Pipes.Count);
        var nearest = new FluidCellPlanner().Find(map, "agent", Refinery, "pipe", deposit.Position, false, false, ["crude-oil"], Route, keep: 1)!;
        var chosen = new FluidCellPlanner().Find(map, "agent", Refinery, "pipe", deposit.Position, false, false, ["crude-oil"], Route)!;
        Assert.True(Pipes(chosen) < Pipes(nearest), $"{Pipes(chosen)} pipes should beat the nearest site's {Pipes(nearest)}.");
    }

    [Fact]
    public void FluidCellsStayOffDeposits()
    {
        var deposits = Enumerable.Range(0, 6).Select(i => OilMaps.Crude($"crude-{i}", new(-10.5 + i * 3, -6.5), 300000)).ToArray();
        var (map, stock) = OilMaps.WithGasSource(deposits);
        var site = new FluidCellPlanner().Find(map, "agent", Chemical, "pipe", new(-4, -6), true, true, ["petroleum-gas"],
            (current, fluid) => new FluidSupplyPlanner().Find(current, stock, "pipe", FluidCellPlanner.PlannedId, fluid))!;
        Assert.All(site.Layout.Entities, e => Assert.DoesNotContain(deposits, d => d.Bounds.Overlaps(Box(map, e))));
    }

    [Fact]
    public void NoSiteIsReportedWhenTheSourceCannotBeReached()
    {
        var (map, stock) = OilMaps.WithGasSource([]);
        // A wall on the tile in front of the refinery gas port: no pipe can leave it.
        map = map with { Entities = [.. map.Entities, OilMaps.Wall("plug", new(-12.5, -2.5))] };
        Assert.Null(new FluidCellPlanner().Find(map, "agent", Chemical, "pipe", new(-10, 0), true, true, ["petroleum-gas"],
            (current, fluid) => new FluidSupplyPlanner().Find(current, stock, "pipe", FluidCellPlanner.PlannedId, fluid), maximumRouted: 8));
    }

    [Fact]
    public void ExtractorsSitOnAFreeDepositFacingAnOpenPortTile()
    {
        var deposit = OilMaps.Crude("crude", new(8.5, .5), 150000);
        // A wall in front of the north-facing port (9.5, -1.5) forces another orientation.
        var map = OilMaps.Map([deposit, OilMaps.Wall("wall", new(9.5, -1.5))]);
        var site = new FluidCellPlanner().FindExtractor(map, Pumpjack, "crude-oil", "pipe")!;
        Assert.Equal("crude", site.ResourceId);
        Assert.Equal(150000, site.Amount);
        var machine = site.Layout.Machine;
        Assert.Equal(deposit.Position, machine.Position);
        var placement = new PlacementCandidate(machine.Position, machine.Direction, 0);
        var target = FluidCellPlanner.Ports(map.Prototypes["pumpjack"], placement).Single().TargetPosition;
        Assert.NotEqual(new MapPosition(9.5, -1.5), target);
        Assert.NotEqual(target, site.Layout.Role("pole")!.Position);
    }

    [Fact]
    public void AnOccupiedDepositIsNotOfferedAgain()
    {
        var deposit = OilMaps.Crude("crude", new(8.5, .5), 150000);
        var installed = new SpatialEntity("jack", "pumpjack", deposit.Position,
            new WorldBox(new(-1.2, -1.2), new(1.2, 1.2)).Translate(deposit.Position), 0, "agent");
        Assert.Null(new FluidCellPlanner().FindExtractor(OilMaps.Map([deposit, installed]), Pumpjack, "crude-oil", "pipe"));
    }

    private static WorldBox Box(SpatialSnapshot map, PlannedEntity e) =>
        map.Prototypes[map.Items[e.Item].EntityName].CollisionBox.Rotate(e.Direction).Translate(e.Position);
    private static MapPosition At(PlannedEntity e, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, e.Direction);
        return new(e.Position.X + rotated.X, e.Position.Y + rotated.Y);
    }
    private static (double, double) Tile(MapPosition p) => (Math.Floor(p.X), Math.Floor(p.Y));
}

/// <summary>Base-game 2.0.77 oil geometry from entities.lua, mining-drill.lua and resources.lua on the synthetic grass map.</summary>
internal static class OilMaps
{
    private static readonly CollisionMask Solid = new(["item", "object", "player", "water_tile"], false, false, false);

    private static FluidPortGeometry Port(int direction, string flow, MapPosition north) => new(1, "normal", direction, flow,
        [north, ExtractionPlanner.Rotate(north, 4), ExtractionPlanner.Rotate(north, 8), ExtractionPlanner.Rotate(north, 12)], ["default"]);

    public static SpatialSnapshot Map(IReadOnlyList<SpatialEntity> entities)
    {
        var grass = FactoryMaps.Grass(24, entities);
        static WorldBox Box(double h) => new(new(-h, -h), new(h, h));
        var prototypes = new Dictionary<string, EntityGeometry>(grass.Prototypes)
        {
            ["chemical-plant"] = new("chemical-plant", "assembling-machine", Box(1.2), Solid, 3, 3, IsElectric: true, FluidBoxes:
                [new(1, "input", [Port(0, "input", new(-1, -1))]), new(2, "input", [Port(0, "input", new(1, -1))]),
                 new(3, "output", [Port(8, "output", new(-1, 1))]), new(4, "output", [Port(8, "output", new(1, 1))])]),
            ["oil-refinery"] = new("oil-refinery", "assembling-machine", Box(2.4), Solid, 5, 5, IsElectric: true, FluidBoxes:
                [new(1, "input", [Port(8, "input", new(-1, 2))]), new(2, "input", [Port(8, "input", new(1, 2))]),
                 new(3, "output", [Port(0, "output", new(-2, -2))]), new(4, "output", [Port(0, "output", new(0, -2))]),
                 new(5, "output", [Port(0, "output", new(2, -2))])]),
            // The pumpjack lists its port positions per direction instead of rotating one.
            ["pumpjack"] = new("pumpjack", "mining-drill", Box(1.2), Solid, 3, 3, MiningRadius: .49, IsElectric: true, MiningSpeed: 1,
                ResourceCategories: new Dictionary<string, bool> { ["basic-fluid"] = true },
                FluidBoxes: [new(1, "output", [new(1, "normal", 0, "output", [new(1, -1), new(1, -1), new(-1, 1), new(-1, 1)], ["default"])])]),
            ["crude-oil"] = new("crude-oil", "resource", Box(1.4), new(["resource"], false, false, false), 1, 1,
                ResourceCategory: "basic-fluid", MiningTime: 1, NormalResourceAmount: 300000, InfiniteResource: true),
            ["pipe"] = new("pipe", "pipe", Box(.3), Solid, 1, 1, FluidBoxes: [new(1, "input-output", [])]),
            ["wall"] = new("wall", "wall", Box(.3), Solid, 1, 1)
        };
        var items = new Dictionary<string, PlaceableItem>(grass.Items)
        {
            ["chemical-plant"] = new("chemical-plant", 10), ["oil-refinery"] = new("oil-refinery", 10),
            ["pumpjack"] = new("pumpjack", 20), ["pipe"] = new("pipe", 100)
        };
        return grass with { Prototypes = prototypes, Items = items };
    }

    public static SpatialEntity Crude(string id, MapPosition at, double amount) =>
        new(id, "crude-oil", at, new WorldBox(new(-1.4, -1.4), new(1.4, 1.4)).Translate(at), 0, "neutral", amount);

    public static SpatialEntity Wall(string id, MapPosition at) => new(id, "wall", at, new WorldBox(new(-.3, -.3), new(.3, .3)).Translate(at), 0, "agent");

    /// <summary>A configured refinery at (-14.5, 0.5) holding gas behind its native output port at (-12.5, -1.5).</summary>
    public static (SpatialSnapshot Map, FactorySnapshot Stock) WithGasSource(IReadOnlyList<SpatialEntity> others)
    {
        var refinery = new SpatialEntity("refinery", "oil-refinery", new(-14.5, .5), new WorldBox(new(-2.4, -2.4), new(2.4, 2.4)).Translate(new(-14.5, .5)),
            0, "agent", FluidConnections: [new(5, 1, new(-12.5, -1.5), new(-12.5, -2.5), Type: "normal", FlowDirection: "output", Filter: "petroleum-gas")]);
        var map = Map([refinery, .. others]);
        var record = new FactoryRecord("gas", "fluid", "refinery", "fluid", Protocol.ToElement(new
        {
            aggregateSafe = true, contents = new Dictionary<string, double> { ["petroleum-gas"] = 90 },
            sourceBoxes = new[] { new { entityId = "refinery", index = 5 } }
        }));
        return (map, new FactorySnapshot("stock", map.Scope, 1, 100, Protocol.ToElement(new { }), [record]));
    }
}
