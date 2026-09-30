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

    [Fact]
    public void TwoTileFurnacesUseTheGapColumnForTheirPole()
    {
        var map = FactoryMaps.Grass(40);
        var layout = new FactoryBandPlanner().Layout(map, Assembler with { Machine = "stone-furnace" }, new(0, 0), new(0, 2, true));
        Assert.Equal(new MapPosition(7, 1), layout.Machine.Position);
        Assert.Equal(new MapPosition(8.5, 2.5), layout.Role("pole")!.Position);
        Assert.Equal(new MapPosition(6.5, 2.5), layout.Role("input-inserter")!.Position);
        Assert.Equal(new MapPosition(7.5, 2.5), layout.Role("output-inserter")!.Position);
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

    public static SpatialSnapshot Grass(int half, IReadOnlyList<SpatialEntity>? entities = null, Func<int, int, string>? tile = null)
    {
        static WorldBox Box(double h) => new(new(-h, -h), new(h, h));
        var prototypes = new Dictionary<string, EntityGeometry>
        {
            ["character"] = new("character", "character", Box(0.19921875), new(["player"], false, false, true), 1, 1),
            ["assembling-machine-1"] = new("assembling-machine-1", "assembling-machine", Box(1.19921875), Solid, 3, 3, IsElectric: true),
            ["lab"] = new("lab", "lab", Box(1.19921875), Solid, 3, 3, IsElectric: true),
            ["stone-furnace"] = new("stone-furnace", "furnace", Box(0.7), Solid, 2, 2),
            ["inserter"] = new("inserter", "inserter", Box(0.1484375), Solid, 1, 1, IsElectric: true,
                InserterPickup: new(0, -1), InserterDrop: new(0, 1.2)),
            ["wooden-chest"] = new("wooden-chest", "container", Box(0.3515625), Solid, 1, 1),
            ["small-electric-pole"] = new("small-electric-pole", "electric-pole", Box(0.1484375), Solid, 1, 1,
                SupplyArea: 2.5, MaxWireDistance: 7.5),
            ["tree"] = new("tree", "tree", Box(0.4), Solid, 1, 1),
            ["gun-turret"] = new("gun-turret", "ammo-turret", Box(0.7), Solid, 2, 2),
            ["stone-wall"] = new("stone-wall", "wall", Box(0.29), Solid, 1, 1),
            ["burner-mining-drill"] = new("burner-mining-drill", "mining-drill", Box(0.7), Solid, 2, 2),
            ["rocket-silo"] = new("rocket-silo", "rocket-silo", Box(4.2), Solid, 9, 9),
            ["iron-ore"] = new("iron-ore", "resource", Box(0.1), new(["resource"], false, false, false), 1, 1, ResourceCategory: "basic-solid")
        };
        var items = new Dictionary<string, PlaceableItem>
        {
            ["assembling-machine-1"] = new("assembling-machine-1", 50),
            ["lab"] = new("lab", 10),
            ["stone-furnace"] = new("stone-furnace", 50),
            ["inserter"] = new("inserter", 50),
            ["wooden-chest"] = new("wooden-chest", 50),
            ["small-electric-pole"] = new("small-electric-pole", 50),
            ["gun-turret"] = new("gun-turret", 10),
            ["stone-wall"] = new("stone-wall", 100)
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
