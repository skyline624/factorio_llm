using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportGroundTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("depleted")]
    public void HistoricalPlansDoNotExhaustNativeGeometryWhileUnfinishedPartsStayReserved(string status)
    {
        var map = FactoryMaps.Grass(24);
        var belt = map.Prototypes["inserter"] with { Name = "transport-belt", Type = "transport-belt" };
        var visible = new SpatialEntity("built-chest", "iron-chest", new(-4.5, .5),
            map.Prototypes["iron-chest"].CollisionBox.Translate(new(-4.5, .5)), 0, "own");
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [belt.Name] = belt }, Entities = [visible] };
        var cells = Enumerable.Range(0, 18).Select(i => Cell($"history-{i}", status, "assembler",
            new("machine", $"distant-equipment-{i}", new(100 + i * 5, 100), 0))).ToArray();
        var unfinished = Cell("pending-boiler", "building", "power", new("machine", "boiler", new(6, 6), 4));
        var rowEquipment = new ResourceCellEquipment("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
        var row = new ResourceRow(1, "smelter", "iron-plate", "iron-ore", rowEquipment, new(12, 12), 0,
            ResourceCellPlanner.Pitch(map, rowEquipment, 0) ?? throw new InvalidDataException("No row geometry."), 2, 18.75);
        var state = new FactoryState(1, "world", [], [.. cells, unfinished,
            Cell("pending-bus", "building", "transport", new("belt", "obsolete-bus-equipment", new(18, 0), 0))], [row]);

        var requested = FactoryTransportBuilder.GeometryItems(state);
        Assert.InRange(requested.Length, 1, 16);
        Assert.Contains("boiler", requested);
        Assert.Contains("electric-mining-drill", requested);
        Assert.DoesNotContain(requested, i => i.StartsWith("distant-", StringComparison.Ordinal) || i == "obsolete-bus-equipment");
        // The native photograph holds item mappings only for the request, but all visible entity prototypes.
        map = map with { Items = requested.ToDictionary(i => i, i => i == belt.Name ? new PlaceableItem(belt.Name, 100) : map.Items[i]) };
        var reserved = FactoryTransportBuilder.ProtectBands(map, state);
        var field = new SpatialCollisionField(reserved);
        Assert.False(field.PlacementClear(belt, new(6, 6), 0));
        Assert.False(field.PlacementClear(belt, visible.Position, 0));
        Assert.All(ResourceCellPlanner.Reservation(map, row), box =>
            Assert.False(field.PlacementClear(belt, new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2), 0)));
        Assert.True(field.PlacementClear(belt, new(-10.5, -10.5), 0));
    }

    [Fact]
    public void FuelExportsCanUseTheirSourceWalkwayWhileEveryMachineSlotStaysReserved()
    {
        var map = FactoryMaps.Grass(24);
        var belt = map.Prototypes["inserter"] with { Name = "transport-belt", Type = "transport-belt" };
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [belt.Name] = belt },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { [belt.Name] = new(belt.Name, 100) } };
        var equipment = new ResourceCellEquipment("electric-mining-drill", "iron-chest", Pole: "small-electric-pole");
        var row = new ResourceRow(1, "miner", "coal", "coal", equipment, new(4, 4), 12,
            ResourceCellPlanner.Pitch(map, equipment, 12) ?? throw new InvalidDataException("No row geometry."), 3, 30);
        var state = new FactoryState(1, "world", [], [], [row]);
        var boxes = ResourceCellPlanner.Reservation(map, row);
        var closed = new SpatialCollisionField(FactoryTransportBuilder.ProtectBands(map, state));
        var exported = new SpatialCollisionField(FactoryTransportBuilder.ProtectBands(map, state, sourceTransportRows: new HashSet<int> { 1 }));
        MapPosition Tile(WorldBox box) => new(Math.Floor(box.Min.X) + .5, Math.Floor(box.Min.Y) + .5);
        Assert.False(closed.PlacementClear(belt, Tile(boxes[^1]), 0));
        Assert.True(exported.PlacementClear(belt, Tile(boxes[^1]), 0));
        Assert.All(boxes.Take(boxes.Count - 1), box => Assert.False(exported.PlacementClear(belt, Tile(box), 0)));
    }

    private static FactoryCell Cell(string id, string status, string kind, PlannedEntity part) =>
        new(id, 0, new(0, 0, true), kind, part.Item, null, new Dictionary<string, string>(), status, 100,
            Plan: new Dictionary<string, PlannedEntity> { [part.Role] = part });
}
