namespace Factorio.Agent.Core;

public enum PowerGridSearchStatus { Connected, Extension, NoObservedPath, SearchBudgetExhausted }
public sealed record PowerGridLink(PowerGridSearchStatus Status, string? SourceId = null, PlacementCandidate? Pole = null);

/// <summary>Searches observed native pole placements and returns one link for construction and re-observation.</summary>
public sealed class PowerGridPlanner
{
    public static string ChoosePole(SpatialSnapshot map, IReadOnlyList<string> obtainableItems, IReadOnlyDictionary<string, long> carried) =>
        obtainableItems.Where(item => map.Items.TryGetValue(item, out var placeable)
            && map.Prototypes[placeable.EntityName] is { Type: "electric-pole", MaxWireDistance: > 0, SupplyArea: > 0 })
            .OrderByDescending(item => carried.GetValueOrDefault(item) > 0)
            .ThenByDescending(item => map.Prototypes[map.Items[item].EntityName].MaxWireDistance)
            .ThenBy(item => item, StringComparer.Ordinal).FirstOrDefault()
        ?? throw new InvalidDataException("No obtainable pole has native wire and supply geometry.");

    private sealed record Node(MapPosition Position, int Cost, string SourceId, MapPosition? First, double WireRange);

    public PowerGridLink Next(SpatialSnapshot map, string poleItem, WorldBox target, IReadOnlySet<string> owned,
        CancellationToken token = default) => Search(map, poleItem, target, owned, token);

    /// <summary>Joins a native pole by wire, including a pole inside a reserved factory band. Its supply area is irrelevant.</summary>
    public PowerGridLink NextToPole(SpatialSnapshot map, string poleItem, string targetPrototype, MapPosition targetPosition,
        IReadOnlySet<string> owned, CancellationToken token = default)
    {
        if (!map.Prototypes.TryGetValue(targetPrototype, out var target)
            || target.Type != "electric-pole" || target.MaxWireDistance is not > 0 or > 64
            || !double.IsFinite(target.MaxWireDistance.Value)
            || !double.IsFinite(targetPosition.X) || !double.IsFinite(targetPosition.Y))
            throw new InvalidDataException("Missing bounded native target pole wire geometry.");
        return Search(map, poleItem, target.CollisionBox.Translate(targetPosition), owned, token,
            targetPosition, target.MaxWireDistance.Value);
    }

    private static PowerGridLink Search(SpatialSnapshot map, string poleItem, WorldBox target, IReadOnlySet<string> owned,
        CancellationToken token, MapPosition? targetPolePosition = null, double targetWireRange = 0)
    {
        var pole = map.Prototypes[map.Items[poleItem].EntityName];
        if (pole.Type != "electric-pole" || pole.MaxWireDistance is not > 0 or > 64 || pole.SupplyArea is not > 0
            || !map.Coverage.Complete || !map.Coverage.Atomic)
            throw new InvalidDataException("Missing bounded native pole geometry or complete local terrain.");
        double wire = pole.MaxWireDistance.Value;
        var sources = map.Entities.Where(e => owned.Contains(e.Id) && map.Prototypes[e.Name].Type == "electric-pole"
            && e.Power?.NetworkId is not null && map.Prototypes[e.Name].MaxWireDistance is > 0).ToArray();
        bool Connects(MapPosition position, EntityGeometry geometry) => targetPolePosition is null
            ? Supplies(position, geometry, target)
            : position.DistanceTo(targetPolePosition) <= Math.Min(geometry.MaxWireDistance!.Value, targetWireRange);
        foreach (var source in sources)
            if (Connects(source.Position, map.Prototypes[source.Name])) return new(PowerGridSearchStatus.Connected, source.Id);
        if (sources.Length == 0) return new(PowerGridSearchStatus.NoObservedPath);
        var center = targetPolePosition ?? new MapPosition((target.Min.X + target.Max.X) / 2, (target.Min.Y + target.Max.Y) / 2);
        double goalRange = targetPolePosition is null ? pole.SupplyArea.Value : Math.Min(wire, targetWireRange);
        double initialDistance = sources.Min(s => s.Position.DistanceTo(center));
        bool localTarget = map.Bounds.Contains(target);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var queue = new PriorityQueue<Node, double>();
        var costs = new Dictionary<MapPosition, int>();
        var clear = new Dictionary<MapPosition, bool>();
        foreach (var source in sources)
        {
            costs[source.Position] = 0;
            queue.Enqueue(new(source.Position, 0, source.Id, null,
                Math.Min(wire, map.Prototypes[source.Name].MaxWireDistance!.Value)), source.Position.DistanceTo(center) / wire);
        }
        int expanded = 0;
        while (queue.TryDequeue(out var node, out _))
        {
            token.ThrowIfCancellationRequested();
            if (costs[node.Position] != node.Cost) continue;
            if (++expanded > 8192) return new(PowerGridSearchStatus.SearchBudgetExhausted);
            if (node.First is not null && (Connects(node.Position, pole)
                || !localTarget && node.Position.DistanceTo(center) <= initialDistance - Math.Min(16, initialDistance / 2)))
                return new(PowerGridSearchStatus.Extension, node.SourceId, new(node.First, 0, node.Cost));
            double range = node.WireRange, alignX = pole.TileWidth % 2 * .5, alignY = pole.TileHeight % 2 * .5;
            for (double x = Math.Ceiling(Math.Max(map.Bounds.Min.X, node.Position.X - range) - alignX) + alignX;
                x <= Math.Min(map.Bounds.Max.X, node.Position.X + range); x++)
                for (double y = Math.Ceiling(Math.Max(map.Bounds.Min.Y, node.Position.Y - range) - alignY) + alignY;
                    y <= Math.Min(map.Bounds.Max.Y, node.Position.Y + range); y++)
                {
                    var point = new MapPosition(x, y);
                    if (point == node.Position || point.DistanceTo(node.Position) > range
                        || costs.TryGetValue(point, out int old) && old <= node.Cost + 1) continue;
                    if (!clear.TryGetValue(point, out bool allowed))
                    {
                        allowed = !target.Overlaps(pole.CollisionBox.Translate(point)) && field.PlacementClear(pole, point, 0);
                        clear[point] = allowed;
                    }
                    if (!allowed) continue;
                    costs[point] = node.Cost + 1;
                    queue.Enqueue(new(point, node.Cost + 1, node.SourceId, node.First ?? point, wire),
                        node.Cost + 1 + Math.Max(0, point.DistanceTo(center) - goalRange) / wire);
                }
        }
        return new(PowerGridSearchStatus.NoObservedPath);
    }

    /// <summary>
    /// Poles an extension's planned chain places, up to the target or to the local waypoint toward a distant one:
    /// the first pole's score is the chain cost. Zero when no pole is needed.
    /// </summary>
    public static int ChainPoles(PowerGridLink link) => link is { Status: PowerGridSearchStatus.Extension, Pole: { } first }
        ? Math.Max(1, (int)Math.Round(first.Score)) : 0;

    public static bool Supplies(MapPosition position, EntityGeometry pole, WorldBox target) => pole.SupplyArea is > 0
        && new WorldBox(new(position.X - pole.SupplyArea.Value, position.Y - pole.SupplyArea.Value),
            new(position.X + pole.SupplyArea.Value, position.Y + pole.SupplyArea.Value)).Overlaps(target);
}
