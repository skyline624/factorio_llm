namespace Factorio.Agent.Core;

public sealed record CellEquipment(string Machine, string Inserter, string Chest, string Pole);
public sealed record PlannedEntity(string Role, string Item, MapPosition Position, int Direction);
/// <summary>A slot in a factory band: machines face a shared walkway, north row above it, south row below it.</summary>
public sealed record CellSlot(int Band, int Index, bool North);
public sealed record CellLayout(CellSlot Slot, IReadOnlyList<PlannedEntity> Entities, WorldBox Footprint, WorldBox Walkway)
{
    public PlannedEntity Machine => Entities.Single(e => e.Role == "machine");
    public PlannedEntity? Role(string role) => Entities.SingleOrDefault(e => e.Role == role);
}

/// <summary>
/// Synthesizes chest-fed machine cells from native geometry. Machines of one band share a walkway between their
/// chest rows, so every built cell stays reachable and the actor cannot enclose itself. Inserter directions are
/// solved from the prototype pickup and drop vectors, never assumed.
/// </summary>
public sealed class FactoryBandPlanner
{
    public static int Pitch(EntityGeometry machine) => Math.Max(machine.TileWidth, 3);
    public static int BandHeight(EntityGeometry machine) => 2 * machine.TileHeight + 6;

    public CellLayout Layout(SpatialSnapshot map, CellEquipment equipment, MapPosition origin, CellSlot slot,
        bool input = true, bool output = true)
    {
        EntityGeometry machine = Geometry(map, equipment.Machine);
        EntityGeometry arm = Geometry(map, equipment.Inserter);
        EntityGeometry chest = Geometry(map, equipment.Chest);
        EntityGeometry pole = Geometry(map, equipment.Pole);
        if (origin.X != Math.Floor(origin.X) || origin.Y != Math.Floor(origin.Y)) throw new ArgumentException("Zone origins are tile corners.", nameof(origin));
        if (slot.Band < 0 || slot.Index < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        if (machine.TileWidth < 2 || machine.TileHeight < 1 || arm.Type != "inserter" || arm.InserterPickup is null || arm.InserterDrop is null
            || chest.TileWidth != 1 || chest.TileHeight != 1 || arm.TileWidth != 1 || pole.Type != "electric-pole" || pole.TileWidth != 1)
            throw new InvalidDataException("Band cells require native multi-tile machines and one-tile inserters, chests and poles.");
        int w = machine.TileWidth, h = machine.TileHeight, pitch = Pitch(machine);
        double left = origin.X + slot.Index * pitch;
        double bandTop = origin.Y + slot.Band * BandHeight(machine);
        // Rows from the machine outward: inserters, chests, then the shared two-tile walkway.
        double machineTop = slot.North ? bandTop : bandTop + h + 6;
        double armRow = slot.North ? machineTop + h : machineTop - 1;
        double chestRow = slot.North ? armRow + 1 : armRow - 1;
        double walkTop = slot.North ? chestRow + 1 : chestRow - 2;
        var center = new MapPosition(left + w / 2.0, machineTop + h / 2.0);
        WorldBox body = machine.CollisionBox.Translate(center);
        var entities = new List<PlannedEntity> { new("machine", equipment.Machine, center, 0) };
        if (input)
        {
            var at = Tile(left, armRow);
            entities.Add(new("input-inserter", equipment.Inserter, at, Direction(arm, at, from: Tile(left, chestRow), into: body)));
            entities.Add(new("input-chest", equipment.Chest, Tile(left, chestRow), 0));
        }
        if (output)
        {
            var at = Tile(left + w - 1, armRow);
            entities.Add(new("output-inserter", equipment.Inserter, at, Direction(arm, at, from: body, into: Tile(left + w - 1, chestRow))));
            entities.Add(new("output-chest", equipment.Chest, Tile(left + w - 1, chestRow), 0));
        }
        double poleColumn = w >= 3 ? left + 1 : left + w;
        entities.Add(new("pole", equipment.Pole, Tile(poleColumn, armRow), 0));
        var footprint = new WorldBox(new(left, Math.Min(machineTop, chestRow)), new(left + pitch, Math.Max(machineTop + h, chestRow + 1)));
        return new(slot, entities, footprint, new(new(left, walkTop), new(left + pitch, walkTop + 2)));
    }

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));

    private static MapPosition Tile(double x, double y) => new(x + .5, y + .5);

    private static int Direction(EntityGeometry arm, MapPosition at, MapPosition from, WorldBox into) =>
        Direction(arm, at, pickup => ExtractionPlanner.DropTile(pickup) == ExtractionPlanner.DropTile(from), into.Contains);

    private static int Direction(EntityGeometry arm, MapPosition at, WorldBox from, MapPosition into) =>
        Direction(arm, at, from.Contains, drop => ExtractionPlanner.DropTile(drop) == ExtractionPlanner.DropTile(into));

    private static int Direction(EntityGeometry arm, MapPosition at, Func<MapPosition, bool> pickup, Func<MapPosition, bool> drop)
    {
        for (int direction = 0; direction < 16; direction += 4)
        {
            MapPosition p = ExtractionPlanner.Rotate(arm.InserterPickup!, direction), d = ExtractionPlanner.Rotate(arm.InserterDrop!, direction);
            if (pickup(new(at.X + p.X, at.Y + p.Y)) && drop(new(at.X + d.X, at.Y + d.Y))) return direction;
        }
        throw new InvalidDataException("No cardinal inserter direction joins the planned chest and machine.");
    }
}
