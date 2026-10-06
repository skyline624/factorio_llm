namespace Factorio.Agent.Core;

public sealed record CellEquipment(string Machine, string Inserter, string Chest, string Pole);
public sealed record PlannedEntity(string Role, string Item, MapPosition Position, int Direction, string? UndergroundType = null);
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
    public static int Pitch(EntityGeometry machine, bool transportAccess = false) => Math.Max(machine.TileWidth, 3) + (transportAccess ? 4 : 0);
    public static int WalkwayTiles(bool transportAccess) => transportAccess ? 6 : 2;
    public static int BandHeight(EntityGeometry machine, bool transportAccess = false) =>
        2 * (machine.TileHeight + (transportAccess ? 4 : 2)) + WalkwayTiles(transportAccess);

    /// <summary>The walkway both rows of a band share, preserving its native layout and transport access.</summary>
    public static WorldBox Walkway(MapPosition origin, int slots, int pitch, int bandHeight, bool transportAccess = false)
    {
        int tiles = WalkwayTiles(transportAccess);
        double top = origin.Y + (bandHeight - tiles) / 2;
        return new(new(origin.X, top), new(origin.X + slots * pitch, top + tiles));
    }

    public CellLayout Layout(SpatialSnapshot map, CellEquipment equipment, MapPosition origin, CellSlot slot,
        bool input = true, bool output = true, bool transportAccess = false)
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
        int w = machine.TileWidth, h = machine.TileHeight, pitch = Pitch(machine, transportAccess);
        double left = origin.X + slot.Index * pitch;
        // A machine narrower than the pitch sits east, leaving the pole in the west gap column: like a 3-tile machine's
        // centre pole, the first slot's pole then stays within supply reach of a link from outside the reserved band.
        double machineLeft = transportAccess ? left + 2 : left + pitch - w;
        double bandTop = origin.Y + slot.Band * BandHeight(machine, transportAccess);
        int walkwayTiles = WalkwayTiles(transportAccess);
        int rowHeight = (BandHeight(machine, transportAccess) - walkwayTiles) / 2;
        // Rows from the machine outward: inserters, chests, then the shared two-tile walkway.
        double machineTop = slot.North ? bandTop + (transportAccess ? 2 : 0) : bandTop + rowHeight + walkwayTiles + 2;
        double armRow = slot.North ? machineTop + h : machineTop - 1;
        double chestRow = slot.North ? armRow + 1 : armRow - 1;
        double walkTop = bandTop + rowHeight;
        var center = new MapPosition(machineLeft + w / 2.0, machineTop + h / 2.0);
        WorldBox body = machine.CollisionBox.Translate(center);
        var entities = new List<PlannedEntity> { new("machine", equipment.Machine, center, 0) };
        if (input)
        {
            var at = Tile(machineLeft, armRow);
            entities.Add(new("input-inserter", equipment.Inserter, at, Direction(arm, at, from: Tile(machineLeft, chestRow), into: body)));
            entities.Add(new("input-chest", equipment.Chest, Tile(machineLeft, chestRow), 0));
        }
        if (output)
        {
            // A second chest two tiles along the same face occupies a belt port of the first. Put the output
            // behind the machine; the input then has three external sides for independently filtered buses.
            double outputColumn = transportAccess ? machineLeft : machineLeft + w - 1;
            double outputArmRow = transportAccess ? (slot.North ? machineTop - 1 : machineTop + h) : armRow;
            double outputChestRow = transportAccess ? (slot.North ? outputArmRow - 1 : outputArmRow + 1) : chestRow;
            var at = Tile(outputColumn, outputArmRow);
            var to = Tile(outputColumn, outputChestRow);
            entities.Add(new("output-inserter", equipment.Inserter, at, Direction(arm, at, from: body, into: to)));
            entities.Add(new("output-chest", equipment.Chest, to, 0));
        }
        double poleColumn = w >= 3 ? left + 1 : left;
        var polePosition = transportAccess ? Tile(machineLeft - 1, machineTop + Math.Floor(h / 2.0)) : Tile(poleColumn, armRow);
        entities.Add(new("pole", equipment.Pole, polePosition, 0));
        if (transportAccess && entities.Where(e => e.Role is "machine" or "input-inserter" or "output-inserter")
            .Any(e => !PowerGridPlanner.Supplies(polePosition, pole, Geometry(map, e.Item).CollisionBox.Rotate(e.Direction).Translate(e.Position))))
            throw new InvalidDataException("The native pole cannot supply both faces of this transport cell.");
        double firstChest = entities.Where(e => e.Role.EndsWith("chest", StringComparison.Ordinal)).Select(e => e.Position.Y - .5).DefaultIfEmpty(machineTop).Min();
        double lastChest = entities.Where(e => e.Role.EndsWith("chest", StringComparison.Ordinal)).Select(e => e.Position.Y + .5).DefaultIfEmpty(machineTop + h).Max();
        var footprint = new WorldBox(new(left, Math.Min(machineTop, firstChest)), new(left + pitch, Math.Max(machineTop + h, lastChest)));
        return new(slot, entities, footprint, new(new(left, walkTop), new(left + pitch, walkTop + walkwayTiles)));
    }

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));

    private static MapPosition Tile(double x, double y) => new(x + .5, y + .5);

    internal static int Direction(EntityGeometry arm, MapPosition at, MapPosition from, WorldBox into) =>
        Direction(arm, at, pickup => ExtractionPlanner.DropTile(pickup) == ExtractionPlanner.DropTile(from), into.Contains);

    internal static int Direction(EntityGeometry arm, MapPosition at, WorldBox from, MapPosition into) =>
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
