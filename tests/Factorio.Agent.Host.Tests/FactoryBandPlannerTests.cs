using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryBandPlannerTests
{
    private static readonly CellEquipment Assembler = new("assembling-machine-1", "inserter", "wooden-chest", "small-electric-pole");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InsertersMoveFromTheInputChestIntoTheMachineAndOutToTheOutputChest(bool north)
    {
        var map = FactoryMaps.Grass(40);
        var layout = new FactoryBandPlanner().Layout(map, Assembler, new(-10, -10), new(0, 1, north));
        var machine = layout.Machine;
        var body = map.Prototypes["assembling-machine-1"].CollisionBox.Translate(machine.Position);
        var arm = map.Prototypes["inserter"];
        var input = layout.Role("input-inserter")!;
        var output = layout.Role("output-inserter")!;
        Assert.True(body.Contains(At(input, arm.InserterDrop!)));
        Assert.Equal(Tile(layout.Role("input-chest")!.Position), Tile(At(input, arm.InserterPickup!)));
        Assert.True(body.Contains(At(output, arm.InserterPickup!)));
        Assert.Equal(Tile(layout.Role("output-chest")!.Position), Tile(At(output, arm.InserterDrop!)));
    }

    [Fact]
    public void AdjacentSlotsAndMirroredRowsNeverOverlapAndKeepTheWalkwayClear()
    {
        var map = FactoryMaps.Grass(60);
        var planner = new FactoryBandPlanner();
        var layouts = Enumerable.Range(0, 4).SelectMany(i => new[] { true, false }.Select(north =>
            planner.Layout(map, Assembler, new(-20, -20), new(i / 2, i % 2, north)))).ToArray();
        var entities = layouts.SelectMany(l => l.Entities).ToArray();
        var boxes = entities.Select(e => map.Prototypes[map.Items[e.Item].EntityName].CollisionBox.Rotate(e.Direction).Translate(e.Position)).ToArray();
        for (int a = 0; a < boxes.Length; a++)
            for (int b = a + 1; b < boxes.Length; b++)
                Assert.False(boxes[a].Overlaps(boxes[b]), $"{entities[a]} overlaps {entities[b]}");
        foreach (var layout in layouts)
            Assert.DoesNotContain(boxes, box => box.Overlaps(layout.Walkway));
        var field = new SpatialCollisionField(map);
        foreach (var e in entities)
            Assert.True(field.PlacementClear(map.Prototypes[map.Items[e.Item].EntityName], e.Position, e.Direction));
    }

    [Fact]
    public void EachPoleSuppliesItsMachineAndInsertersAndReachesTheNextSlot()
    {
        var map = FactoryMaps.Grass(40);
        var planner = new FactoryBandPlanner();
        var first = planner.Layout(map, Assembler, new(-10, -10), new(0, 0, true));
        var next = planner.Layout(map, Assembler, new(-10, -10), new(0, 1, true));
        var south = planner.Layout(map, Assembler, new(-10, -10), new(0, 0, false));
        var pole = map.Prototypes["small-electric-pole"];
        foreach (var e in first.Entities.Where(e => e.Role != "pole" && !e.Role.EndsWith("chest")))
            Assert.True(PowerGridPlanner.Supplies(first.Role("pole")!.Position, pole,
                map.Prototypes[map.Items[e.Item].EntityName].CollisionBox.Rotate(e.Direction).Translate(e.Position)));
        Assert.True(first.Role("pole")!.Position.DistanceTo(next.Role("pole")!.Position) <= pole.MaxWireDistance);
        Assert.True(first.Role("pole")!.Position.DistanceTo(south.Role("pole")!.Position) <= pole.MaxWireDistance);
    }

    [Theory]
    [InlineData("assembling-machine-1")]
    [InlineData("stone-furnace")]
    public void TheBandWalkwayIsTheOneEveryCellLayoutLeavesFree(string machine)
    {
        var map = FactoryMaps.Grass(60);
        var geometry = map.Prototypes[machine];
        var origin = new MapPosition(-20, -15);
        var walkway = FactoryBandPlanner.Walkway(origin, 4, FactoryBandPlanner.Pitch(geometry), FactoryBandPlanner.BandHeight(geometry));
        var layouts = Enumerable.Range(0, 4).SelectMany(i => new[] { true, false }.Select(north =>
            new FactoryBandPlanner().Layout(map, Assembler with { Machine = machine }, origin, new(0, i, north)))).ToArray();
        // Every slot's walkway is a piece of the band's, which spans the four slots exactly.
        Assert.All(layouts, l => Assert.True(walkway.Contains(l.Walkway)));
        Assert.Equal(4 * FactoryBandPlanner.Pitch(geometry), walkway.Width);
        Assert.Equal(2, walkway.Height);
    }

    [Fact]
    public void TwoTileFurnacesUseTheGapColumnForTheirPole()
    {
        var map = FactoryMaps.Grass(40);
        var layout = new FactoryBandPlanner().Layout(map, Assembler with { Machine = "stone-furnace" }, new(0, 0), new(0, 2, true));
        // The pole takes the west gap column so a link from outside the band reaches the first slot.
        Assert.Equal(new MapPosition(8, 1), layout.Machine.Position);
        Assert.Equal(new MapPosition(6.5, 2.5), layout.Role("pole")!.Position);
        Assert.Equal(new MapPosition(7.5, 2.5), layout.Role("input-inserter")!.Position);
        Assert.Equal(new MapPosition(8.5, 2.5), layout.Role("output-inserter")!.Position);
    }

    [Fact]
    public void ZoneAvoidsWaterAndOreButReportsTreesForClearing()
    {
        var entities = new List<SpatialEntity>
        {
            new("tree-1", "tree", new(3.5, 3.5), new(new(3.1, 3.1), new(3.9, 3.9)), 0, "neutral"),
            new("ore-1", "iron-ore", new(-15.5, 0.5), new(new(-15.6, 0.4), new(-15.4, 0.6)), 0, "neutral", Amount: 500)
        };
        // Water covers the whole west half.
        var map = FactoryMaps.Grass(30, entities, (x, _) => x < -5 ? "water" : "grass");
        var machine = map.Prototypes["assembling-machine-1"];
        var site = new FactoryZonePlanner().Find(map, machine, new(0, 0), 4)!;
        var area = new WorldBox(new(site.Origin.X - 1, site.Origin.Y - 1),
            new(site.Origin.X + 4 * FactoryBandPlanner.Pitch(machine) + 1, site.Origin.Y + FactoryBandPlanner.BandHeight(machine) + 1));
        Assert.True(area.Min.X >= -5);
        Assert.False(area.Contains(new MapPosition(-15.5, 0.5)));
        Assert.Contains(site.Clearance, e => e.Id == "tree-1");
        Assert.NotNull(FactoryZonePlanner.Clearance(map, area));
        Assert.Null(FactoryZonePlanner.Clearance(map, new(new(-10, 0), new(-2, 4))));
    }

    [Fact]
    public void ZoneIsAbsentWhenNoRectangleIsBuildable()
    {
        var map = FactoryMaps.Grass(12, tile: (_, _) => "water");
        Assert.Null(new FactoryZonePlanner().Find(map, map.Prototypes["assembling-machine-1"], new(0, 0), 2));
    }

    [Fact]
    public void PowerLinksRouteAroundTheReservedBand()
    {
        var box = new WorldBox(new(-0.15, -0.15), new(0.15, 0.15));
        var source = new SpatialEntity("src", "small-electric-pole", new(-18.5, 0.5), box.Translate(new(-18.5, 0.5)), 0, "agent", Power: new(1, 1));
        var cellPole = new SpatialEntity("cell", "small-electric-pole", new(-29.5, 5.5), box.Translate(new(-29.5, 5.5)), 0, "agent", Power: new(0, 2));
        var map = FactoryMaps.Grass(40, [source, cellPole]);
        var zone = new FactoryZone(1, new(-31, 2), 8, 3, 12);
        var zoneBox = new WorldBox(zone.Origin, new(-7, 14));
        var free = new PowerGridPlanner().Next(map, "small-electric-pole", cellPole.Bounds, new HashSet<string> { "src" });
        Assert.True(zoneBox.Contains(free.Pole!.Position), "Without a reservation the shortest link enters the band.");
        var reserved = new PowerGridPlanner().Next(FactoryCellBuilder.ReserveZone(map, zone, "small-electric-pole"), "small-electric-pole",
            cellPole.Bounds, new HashSet<string> { "src" });
        Assert.Equal(PowerGridSearchStatus.Extension, reserved.Status);
        Assert.False(zoneBox.Contains(reserved.Pole!.Position));
    }

    [Fact]
    public void EveryReservedZoneBlocksPlacementEvenWhenBandsTouch()
    {
        var map = FactoryMaps.Grass(40);
        var west = new FactoryZone(1, new(-20, 0), 2, 3, 12);
        var east = new FactoryZone(2, new(-14, 0), 2, 3, 12);
        var reserved = FactoryCellBuilder.ReserveZone(FactoryCellBuilder.ReserveZone(map, west, "small-electric-pole"), east, "small-electric-pole");
        Assert.False(new SpatialCollisionField(reserved).PlacementClear(map.Prototypes["small-electric-pole"], new(-13.5, 5.5), 0));
    }

    private static MapPosition At(PlannedEntity e, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, e.Direction);
        return new(e.Position.X + rotated.X, e.Position.Y + rotated.Y);
    }
    private static (double, double) Tile(MapPosition p) => (Math.Floor(p.X), Math.Floor(p.Y));
}

/// <summary>Synthetic maps with base-game 2.0.77 geometry recorded from native spatial snapshots.</summary>
internal static class FactoryMaps
{
    private static readonly CollisionMask Solid = new(["item", "object", "player", "water_tile"], false, false, false);
    private static readonly CollisionMask Ground = new(["ground_tile"], false, false, false);
    private static readonly CollisionMask Water = new(["water_tile", "item", "player"], false, false, false);
    private static readonly Dictionary<string, bool> SolidOre = new() { ["basic-solid"] = true };

    // resources.lua: every base ore has mining_time 1 and a 0.1 collision box.
    private static EntityGeometry Deposit(string name) => new(name, "resource", new(new(-0.1, -0.1), new(0.1, 0.1)),
        new(["resource"], false, false, false), 1, 1, ResourceCategory: "basic-solid", MiningTime: 1);

    /// <summary>One deposit entity per tile of the rectangle [x0, x1) x [y0, y1).</summary>
    public static IEnumerable<SpatialEntity> Patch(string name, int x0, int y0, int x1, int y1, double amount = 5000)
    {
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                yield return new($"{name}:{x}:{y}", name, new(x + .5, y + .5), new(new(x + .4, y + .4), new(x + .6, y + .6)), 0, "neutral", amount);
    }

    public static SpatialSnapshot Grass(int half, IReadOnlyList<SpatialEntity>? entities = null, Func<int, int, string>? tile = null)
    {
        static WorldBox Box(double h) => new(new(-h, -h), new(h, h));
        static FluidPortGeometry Port(int index, int direction, string flow, params MapPosition[] positions) =>
            new(index, "normal", direction, flow, positions, ["default"]);
        var prototypes = new Dictionary<string, EntityGeometry>
        {
            ["character"] = new("character", "character", Box(0.19921875), new(["player"], false, false, true), 1, 1),
            ["assembling-machine-1"] = new("assembling-machine-1", "assembling-machine", Box(1.19921875), Solid, 3, 3, IsElectric: true, EnergyPerTick: 1250),
            ["lab"] = new("lab", "lab", Box(1.19921875), Solid, 3, 3, IsElectric: true, EnergyPerTick: 1000),
            ["stone-furnace"] = new("stone-furnace", "furnace", Box(0.7), Solid, 2, 2),
            ["inserter"] = new("inserter", "inserter", Box(0.1484375), Solid, 1, 1, IsElectric: true,
                InserterPickup: new(0, -1), InserterDrop: new(0, 1.2), EnergyPerTick: 245),
            ["wooden-chest"] = new("wooden-chest", "container", Box(0.3515625), Solid, 1, 1),
            ["small-electric-pole"] = new("small-electric-pole", "electric-pole", Box(0.1484375), Solid, 1, 1,
                SupplyArea: 2.5, MaxWireDistance: 7.5),
            ["tree"] = new("tree", "tree", Box(0.4), Solid, 1, 1),
            ["iron-chest"] = new("iron-chest", "container", Box(0.3515625), Solid, 1, 1),
            // mining-drill.lua: collision, resource_searching_radius, vector_to_place_result and mining_speed.
            ["electric-mining-drill"] = new("electric-mining-drill", "mining-drill", Box(1.3515625), Solid, 3, 3, MiningRadius: 2.49,
                MiningOutput: new(0, -1.85), ResourceCategories: SolidOre, IsElectric: true, MiningSpeed: 0.5, EnergyPerTick: 1500),
            ["burner-mining-drill"] = new("burner-mining-drill", "mining-drill", Box(0.69921875), Solid, 2, 2, MiningRadius: 0.99,
                MiningOutput: new(-0.5, -1.3), ResourceCategories: SolidOre, FuelCategories: new Dictionary<string, bool> { ["chemical"] = true },
                MiningSpeed: 0.25, EnergyPerTick: 2500, BurnerEffectivity: 1),
            ["iron-ore"] = Deposit("iron-ore"),
            ["copper-ore"] = Deposit("copper-ore"),
            ["coal"] = Deposit("coal"),
            ["stone"] = Deposit("stone"),
            ["boiler"] = new("boiler", "boiler", new(new(-1.2890625, -0.7890625), new(1.2890625, 0.7890625)), Solid, 3, 2,
                FuelCategories: new Dictionary<string, bool> { ["chemical"] = true }, EnergyPerTick: 30000, BurnerEffectivity: 1, FluidBoxes:
                [new(1, "input", [Port(1, 12, "input-output", new(-1, .5), new(-.5, -1), new(1, -.5), new(.5, 1)),
                    Port(2, 4, "input-output", new(1, .5), new(-.5, 1), new(-1, -.5), new(.5, -1))], "water"),
                 new(2, "output", [Port(1, 0, "output", new(0, -.5), new(.5, 0), new(0, .5), new(-.5, 0))], "steam")]),
            ["steam-engine"] = new("steam-engine", "generator", new(new(-1.25, -2.34765625), new(1.25, 2.34765625)), Solid, 3, 5,
                IsElectric: true, EnergyPerTick: 0, MaxPowerOutput: 15000, FluidBoxes:
                [new(1, "input", [Port(1, 8, "input-output", new(0, 2), new(-2, 0), new(0, -2), new(2, 0)),
                    Port(2, 0, "input-output", new(0, -2), new(2, 0), new(0, 2), new(-2, 0))], "steam", MinimumTemperature: 100)]),
            ["offshore-pump"] = new("offshore-pump", "offshore-pump", new(new(-0.59765625, -1.046875), new(0.59765625, 0.296875)),
                new(["is_lower_object", "is_object", "object", "train"], false, false, false), 1, 1, FluidSourceOffset: new(0, -1),
                FluidBoxes: [new(1, "output", [Port(1, 8, "output", new(0, 0), new(0, 0), new(0, 0), new(0, 0))])],
                TileBuildability: [new(Box(0.3984375), new(["water_tile"], false, false, false), Ground),
                    new(new(new(-1, -2), new(1, -1)), new([], false, false, false), new(["water_tile"], false, false, false))]),
            ["gun-turret"] = new("gun-turret", "ammo-turret", Box(0.7), Solid, 2, 2),
            ["stone-wall"] = new("stone-wall", "wall", Box(0.29), Solid, 1, 1),
            ["rocket-silo"] = new("rocket-silo", "rocket-silo", Box(4.2), Solid, 9, 9),
            // collision-mask-defaults.lua: belts collide with buildings, water and other belts, never with the character.
            ["transport-belt"] = new("transport-belt", "transport-belt", Box(0.4),
                new(["floor", "meltable", "object", "transport_belt", "water_tile"], false, false, false), 1, 1, BeltSpeed: 0.03125)
        };
        var items = new Dictionary<string, PlaceableItem>
        {
            ["assembling-machine-1"] = new("assembling-machine-1", 50),
            ["lab"] = new("lab", 10),
            ["stone-furnace"] = new("stone-furnace", 50),
            ["inserter"] = new("inserter", 50),
            ["wooden-chest"] = new("wooden-chest", 50),
            ["iron-chest"] = new("iron-chest", 50),
            ["small-electric-pole"] = new("small-electric-pole", 50),
            ["electric-mining-drill"] = new("electric-mining-drill", 50),
            ["burner-mining-drill"] = new("burner-mining-drill", 50),
            ["boiler"] = new("boiler", 50),
            ["steam-engine"] = new("steam-engine", 10),
            ["offshore-pump"] = new("offshore-pump", 20),
            ["gun-turret"] = new("gun-turret", 10),
            ["stone-wall"] = new("stone-wall", 100),
            ["transport-belt"] = new("transport-belt", 100)
        };
        var rows = new List<TileRun>();
        for (int y = -half; y < half; y++)
            for (int x = -half; x < half; x++)
                rows.Add(new(x, y, 1, tile?.Invoke(x, y) ?? "grass"));
        return new(new("world", "session", "actor", 1, 2), 100, 1, new(new(-half, -half), new(half, half)),
            new("actor", "character", new(0, 0), 10, 10, "ai", 2.7), prototypes,
            new Dictionary<string, CollisionMask> { ["grass"] = Ground, ["water"] = Water },
            rows, entities ?? [], items, new(true, true, "current-character-local-area", half));
    }
}
