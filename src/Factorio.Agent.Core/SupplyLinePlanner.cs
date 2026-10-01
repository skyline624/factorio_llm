namespace Factorio.Agent.Core;

public sealed record SupplyLineEquipment(string Belt, string Inserter, string Chest, string Pole);
/// <summary>The feeder of one planned cell of a row: it picks from the cell's output chest and drops onto the collector tile Drop.</summary>
public sealed record SupplyFeeder(int Cell, PlannedEntity Inserter, MapPosition Drop);
/// <summary>
/// The row side of a supply line: a feeder for every planned cell whose walkway tile is free, the collector belts in flow order along
/// the row's walkway, and Head, the first trunk tile beyond the walkway end the collector flows to.
/// </summary>
public sealed record SupplyCollector(IReadOnlyList<SupplyFeeder> Feeders, IReadOnlyList<PlannedEntity> Belts, MapPosition Head);
/// <summary>The band side: the depot chest, the inserter filling it from the trunk's last belt at Pickup, and whether a fed pole already powers it.</summary>
public sealed record SupplyDepot(PlannedEntity Chest, PlannedEntity Inserter, MapPosition Pickup, bool Powered);
/// <summary>Trunk belts to build in flow order; Next is the head left for the next observation, null once the depot is reached.</summary>
public sealed record SupplyTrunkSegment(IReadOnlyList<PlacementCandidate> Belts, MapPosition? Next);

/// <summary>
/// Synthesizes supply lines that carry a resource row's output to a depot beside a factory band, from native geometry only: inserter
/// pickup and drop vectors choose every arm's direction and tile, the row's own template places the collector along its walkway, and
/// the trunk between them is routed on observed terrain, segment by segment, by <see cref="BeltRoutePlanner"/>.
/// </summary>
public static class SupplyLinePlanner
{
    public const string Kind = "supply-line";
    /// <summary>The depot chest is the line's output chest: logistics collects it like any cell output.</summary>
    public const string DepotChestRole = "output-chest";
    public const string DepotInserterRole = "depot-inserter";
    /// <summary>
    /// Distance from a row's chests to the nearest band walkway beyond which a line pays: the radius within which the controller
    /// navigates directly. Below it the actor collects without leaving the band's surroundings and the line's fixed parts (depot,
    /// inserters, poles) would not repay; beyond it every collection round is a multi-segment trip out of the factory core.
    /// </summary>
    public const double MinimumDistance = 24;
    /// <summary>Trunk belts one line may place, the bound of a native transport line.</summary>
    public const int MaximumTrunkBelts = 200;
    /// <summary>Tiles from a band walkway end within which the depot chest stands: the actor reaches it from that end.</summary>
    public const int DepotRadius = 6;
    /// <summary>Tiles beyond a band walkway end kept free so the actor still enters the band.</summary>
    public const int CorridorLength = 3;

    private static readonly MapPosition[] Units = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];

    public static string FeederRole(int cell) => $"feeder-{cell}";
    public static string CollectorRole(int index) => $"collector-{index:D3}";
    public static string TrunkRole(int index) => $"trunk-{index:D3}";

    /// <summary>
    /// Feeders on the walkway tile beside every planned chest and a collector at their native drop depth, from the farthest planned
    /// cell to the walkway end whose next tile lies nearer <paramref name="toward"/>; the other end is used when that one is blocked.
    /// Null when no inserter direction feeds the walkway, the collector tiles are taken or no feeder tile is free.
    /// </summary>
    public static SupplyCollector? Collector(SpatialSnapshot map, ResourceRow row, SupplyLineEquipment equipment, MapPosition toward)
    {
        var access = ResourceCellPlanner.Access(map, row);
        EntityGeometry arm = Geometry(map, equipment.Inserter), belt = Geometry(map, equipment.Belt);
        if (!Arm(arm) || belt is not { Type: "transport-belt", TileWidth: 1, TileHeight: 1 }) return null;
        var field = Field(map);
        var forbidden = BeltRoutePlanner.Forbidden(map);
        var feeders = new List<SupplyFeeder>();
        double? depth = null;
        for (int cell = 0; cell < access.Chests.Count; cell++)
        {
            MapPosition chest = access.Chests[cell], at = Add(chest, access.Outward);
            if (Feed(arm, at, chest, access) is not { } feed) return null;
            double reach = Dot(Sub(feed.Drop, chest), access.Outward);
            if (depth is not null && depth != reach) return null;
            depth = reach;
            // A feeder tile taken by something else leaves that cell to direct collection; the collector still passes it.
            if (!forbidden.Contains(at) && field.PlacementClear(arm, at, feed.Direction))
                feeders.Add(new(cell, new(FeederRole(cell), equipment.Inserter, at, feed.Direction), feed.Drop));
        }
        if (depth is not { } offset || feeders.Count == 0) return null;
        MapPosition origin = Add(access.Chests[0], Scale(access.Outward, offset));
        MapPosition At(double s) => Add(origin, Scale(access.Along, s - Dot(origin, access.Along)));
        double low = Dot(access.Walkway.Min, access.Along) + .5, high = Dot(access.Walkway.Max, access.Along) - .5;
        double first = Dot(access.Chests[0], access.Along), last = Dot(access.Chests[^1], access.Along);
        bool Placeable(MapPosition p) => !forbidden.Contains(p) && field.PlacementClear(belt, p, 0);
        SupplyCollector? Flow(int sign)
        {
            double from = sign > 0 ? first : last, to = sign > 0 ? high : low;
            var tiles = Enumerable.Range(0, (int)Math.Round(Math.Abs(to - from)) + 1).Select(i => At(from + sign * i)).ToArray();
            MapPosition head = At(to + sign);
            if (!tiles.All(Placeable) || !Placeable(head)) return null;
            int direction = BeltRoutePlanner.Direction(tiles[0], head);
            return new(feeders, tiles.Select((p, i) => new PlannedEntity(CollectorRole(i), equipment.Belt, p, direction)).ToArray(), head);
        }
        var flows = new[] { Flow(1), Flow(-1) }.OfType<SupplyCollector>();
        return flows.OrderBy(f => f.Head.DistanceTo(toward)).FirstOrDefault();
    }

    /// <summary>
    /// A depot chest within <see cref="DepotRadius"/> of the band walkway end nearer <paramref name="toward"/>, with an inserter that
    /// drops into it from the tile where the trunk will end. The corridors beyond both walkway ends stay free; a site already powered
    /// by a fed pole comes first, then the chest nearest the walkway, then the pickup nearest the row. Reservations are the caller's.
    /// </summary>
    public static SupplyDepot? Depot(SpatialSnapshot map, SupplyLineEquipment equipment, WorldBox walkway, MapPosition toward,
        IReadOnlySet<string> fedPoles)
    {
        EntityGeometry arm = Geometry(map, equipment.Inserter), chest = Geometry(map, equipment.Chest), belt = Geometry(map, equipment.Belt);
        if (!Arm(arm) || chest is not { Type: "container", TileWidth: 1, TileHeight: 1 } || belt.Type != "transport-belt") return null;
        MapPosition end = End(walkway, toward);
        var corridors = Corridors(walkway);
        var field = Field(map);
        var forbidden = BeltRoutePlanner.Forbidden(map);
        bool Free(MapPosition p) => !corridors.Any(c => c.Contains(p)) && !forbidden.Contains(p);
        bool Belt(MapPosition p) => Free(p) && field.PlacementClear(belt, p, 0);
        var poles = map.Entities.Where(e => fedPoles.Contains(e.Id) && map.Prototypes[e.Name].Type == "electric-pole").ToArray();
        SupplyDepot? best = null;
        var bestScore = (Unpowered: 0, Walk: 0.0, Trunk: 0.0);
        for (double x = Math.Floor(end.X - DepotRadius) + .5; x <= end.X + DepotRadius; x++)
            for (double y = Math.Floor(end.Y - DepotRadius) + .5; y <= end.Y + DepotRadius; y++)
            {
                var site = new MapPosition(x, y);
                if (site.DistanceTo(end) > DepotRadius || !Free(site) || !field.PlacementClear(chest, site, 0)) continue;
                foreach (var hand in Units.Select(u => Add(site, u)).Where(Free))
                    for (int direction = 0; direction < 16; direction += 4)
                    {
                        MapPosition drop = BeltRoutePlanner.Cell(Add(hand, ExtractionPlanner.Rotate(arm.InserterDrop!, direction)));
                        MapPosition pickup = BeltRoutePlanner.Cell(Add(hand, ExtractionPlanner.Rotate(arm.InserterPickup!, direction)));
                        if (drop != site || pickup == site || pickup == hand || !Belt(pickup) || !field.PlacementClear(arm, hand, direction)) continue;
                        // The trunk arrives at the pickup tile from a free neighbour.
                        if (!Units.Select(u => Add(pickup, u)).Any(n => n != hand && n != site && Belt(n))) continue;
                        var body = arm.CollisionBox.Rotate(direction).Translate(hand);
                        var score = (Unpowered: poles.Any(p => PowerGridPlanner.Supplies(p.Position, map.Prototypes[p.Name], body)) ? 0 : 1,
                            Walk: Math.Round(site.DistanceTo(end), 6), Trunk: Math.Round(pickup.DistanceTo(toward), 6));
                        if (best is not null && score.CompareTo(bestScore) >= 0) continue;
                        best = new(new(DepotChestRole, equipment.Chest, site, 0), new(DepotInserterRole, equipment.Inserter, hand, direction),
                            pickup, score.Unpowered == 0);
                        bestScore = score;
                    }
            }
        return best;
    }

    /// <summary>
    /// The next trunk segment from the head toward the depot pickup tile, continuing the belt that feeds the head. A segment stopping
    /// near the observed edge keeps its last tile as the next head, so the next observation chooses how the line goes on. A trunk
    /// that is a single tile keeps the incoming direction. Null when no route exists on this map.
    /// </summary>
    public static SupplyTrunkSegment? Segment(SpatialSnapshot map, string beltItem, MapPosition head, int incoming, string joined,
        MapPosition pickup, CancellationToken token = default)
    {
        var route = new BeltRoutePlanner().Find(map, beltItem, head, pickup, cancellationToken: token, joined: joined, partial: true);
        return route.Status switch
        {
            BeltRouteStatus.BudgetExceeded => throw new TimeoutException("The trunk route search exhausted its node budget."),
            BeltRouteStatus.NoRouteInSnapshot => null,
            BeltRouteStatus.Found => new(route.Belts.Count == 1 ? [route.Belts[0] with { Direction = incoming }] : route.Belts, null),
            _ => new(route.Belts.Take(route.Belts.Count - 1).ToArray(), route.Belts[^1].Position)
        };
    }

    /// <summary>The tile centre a planned inserter picks from, from its native vector.</summary>
    public static MapPosition PickupTile(EntityGeometry arm, PlannedEntity inserter) =>
        BeltRoutePlanner.Cell(Add(inserter.Position, ExtractionPlanner.Rotate(arm.InserterPickup!, inserter.Direction)));

    /// <summary>The tile centre a planned inserter drops into, from its native vector.</summary>
    public static MapPosition DropTile(EntityGeometry arm, PlannedEntity inserter) =>
        BeltRoutePlanner.Cell(Add(inserter.Position, ExtractionPlanner.Rotate(arm.InserterDrop!, inserter.Direction)));

    /// <summary>The centre of the band walkway end nearer the point, on the free column just outside the band.</summary>
    public static MapPosition End(WorldBox walkway, MapPosition toward)
    {
        double middle = (walkway.Min.Y + walkway.Max.Y) / 2;
        MapPosition west = new(walkway.Min.X - .5, middle), east = new(walkway.Max.X + .5, middle);
        return west.DistanceTo(toward) <= east.DistanceTo(toward) ? west : east;
    }

    /// <summary>The walkway's prolongations beyond both ends, which depots and trunks leave to the actor entering the band.</summary>
    public static IReadOnlyList<WorldBox> Corridors(WorldBox walkway) =>
    [
        new(new(walkway.Min.X - CorridorLength, walkway.Min.Y), new(walkway.Min.X, walkway.Max.Y)),
        new(new(walkway.Max.X, walkway.Min.Y), new(walkway.Max.X + CorridorLength, walkway.Max.Y))
    ];

    /// <summary>The inserter direction at <paramref name="at"/> that picks from the chest tile and drops beyond it, away from the row.</summary>
    private static (int Direction, MapPosition Drop)? Feed(EntityGeometry arm, MapPosition at, MapPosition chest, ResourceRowAccess access)
    {
        for (int direction = 0; direction < 16; direction += 4)
        {
            MapPosition pickup = BeltRoutePlanner.Cell(Add(at, ExtractionPlanner.Rotate(arm.InserterPickup!, direction)));
            MapPosition drop = BeltRoutePlanner.Cell(Add(at, ExtractionPlanner.Rotate(arm.InserterDrop!, direction)));
            if (pickup == BeltRoutePlanner.Cell(chest) && Dot(Sub(drop, at), access.Outward) >= 1 && Dot(Sub(drop, at), access.Along) == 0)
                return (direction, drop);
        }
        return null;
    }

    private static bool Arm(EntityGeometry arm) =>
        arm is { Type: "inserter", IsElectric: true, InserterPickup: not null, InserterDrop: not null, TileWidth: 1, TileHeight: 1 };

    private static SpatialCollisionField Field(SpatialSnapshot map) =>
        new(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));

    private static MapPosition Add(MapPosition a, MapPosition b) => new(a.X + b.X, a.Y + b.Y);
    private static MapPosition Sub(MapPosition a, MapPosition b) => new(a.X - b.X, a.Y - b.Y);
    private static MapPosition Scale(MapPosition a, double factor) => new(a.X * factor, a.Y * factor);
    private static double Dot(MapPosition a, MapPosition b) => a.X * b.X + a.Y * b.Y;
}
