namespace Factorio.Agent.Core;

public enum BeltRouteStatus { Found, NoRouteInSnapshot, BudgetExceeded, Partial }
public sealed record BeltRoutePlan(BeltRouteStatus Status, IReadOnlyList<PlacementCandidate> Belts, int ExpandedNodes);

public sealed class BeltRoutePlanner
{
    public const int EdgeMargin = 6;

    public BeltRoutePlan Find(SpatialSnapshot map, string beltItem, MapPosition start, MapPosition target,
        int nodeBudget = 12000, CancellationToken cancellationToken = default, string? inletBeltId = null,
        IReadOnlySet<string>? existingBusBelts = null, string? joined = null, bool partial = false)
        => FindCore(map,beltItem,start,target,nodeBudget,cancellationToken,inletBeltId,existingBusBelts,null,joined,partial);

    internal BeltRoutePlan FindWithField(SpatialCollisionField field,string beltItem,MapPosition start,MapPosition target,
        int nodeBudget,CancellationToken token) => FindCore(field.Map,beltItem,start,target,nodeBudget,token,null,null,field,null,false);

    private BeltRoutePlan FindCore(SpatialSnapshot map,string beltItem,MapPosition start,MapPosition target,
        int nodeBudget,CancellationToken cancellationToken,string? inletBeltId,IReadOnlySet<string>? existingBusBelts,
        SpatialCollisionField? collisionField, string? joined, bool partial)
    {
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = map.Prototypes[map.Items[beltItem].EntityName];
        if (geometry.Type != "transport-belt" || geometry.TileWidth != 1 || geometry.TileHeight != 1 || geometry.BeltSpeed is not > 0)
            throw new InvalidDataException("Routing requires native one-tile ordinary belt geometry and speed.");
        var field = new BeltRoutingField(map, geometry, start, cancellationToken, inletBeltId, existingBusBelts,collisionField,joined);
        bool Clear(MapPosition p) => field.SurfaceClear(p);
        bool beyond = partial && !map.Bounds.Contains(target);
        if (!Clear(start) || !beyond && !Clear(target)) return new(BeltRouteStatus.NoRouteInSnapshot, [], 0);
        double startDistance = Distance(start, target);
        bool Edge(MapPosition p) => beyond && Distance(p, target) <= startDistance - EdgeMargin
            && (p.X - map.Bounds.Min.X < EdgeMargin || map.Bounds.Max.X - p.X < EdgeMargin
                || p.Y - map.Bounds.Min.Y < EdgeMargin || map.Bounds.Max.Y - p.Y < EdgeMargin);
        // Break equal A* costs toward the target instead of flooding the whole equal-cost rectangle of a long route.
        var frontier = new PriorityQueue<MapPosition, (double Score, double Remaining, int Sequence)>();
        var costs = new Dictionary<MapPosition, int> { [start] = 0 };
        var previous = new Dictionary<MapPosition, MapPosition>();
        var closed = new HashSet<MapPosition>();
        int sequence = 0, expanded = 0;
        frontier.Enqueue(start, (Distance(start, target), Distance(start, target), sequence++));
        while (frontier.TryDequeue(out var current, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!closed.Add(current)) continue;
            if (++expanded > nodeBudget) return new(BeltRouteStatus.BudgetExceeded, [], expanded - 1);
            if (current == target || Edge(current))
            {
                var path = new List<MapPosition> { current };
                while (previous.TryGetValue(current, out var parent)) { path.Add(parent); current = parent; }
                path.Reverse();
                var belts = path.Select((p, i) => new PlacementCandidate(p,
                    path.Count == 1 ? 0 : Direction(i + 1 < path.Count ? p : path[i - 1], i + 1 < path.Count ? path[i + 1] : p), i)).ToArray();
                if (existingBusBelts is not null)
                {
                    int? terminal = new[] { belts[^1].Direction, 0, 4, 8, 12 }.Distinct().Cast<int?>().FirstOrDefault(d =>
                        !path.Contains(Ahead(path[^1], d!.Value)) && !map.Entities.Any(e => map.Prototypes[e.Name].Type is "transport-belt" or "underground-belt" or "splitter"
                            && Cell(e.Position) == Ahead(path[^1], d.Value)));
                    if (terminal is null) return new(BeltRouteStatus.NoRouteInSnapshot, [], expanded);
                    belts[^1] = belts[^1] with { Direction = terminal.Value };
                }
                return new(path[^1] == target ? BeltRouteStatus.Found : BeltRouteStatus.Partial, belts, expanded);
            }
            foreach (var next in new MapPosition[] { new(current.X + 1, current.Y), new(current.X, current.Y + 1), new(current.X - 1, current.Y), new(current.X, current.Y - 1) })
            {
                if (closed.Contains(next) || !Clear(next)) continue;
                int cost = costs[current] + 1;
                if (costs.TryGetValue(next, out int old) && old <= cost) continue;
                costs[next] = cost;
                previous[next] = current;
                double remaining = Distance(next, target);
                frontier.Enqueue(next, (cost + remaining, remaining, sequence++));
            }
        }
        return new(BeltRouteStatus.NoRouteInSnapshot, [], expanded);
    }

    public static MapPosition Cell(MapPosition position) => new(Math.Floor(position.X) + .5, Math.Floor(position.Y) + .5);
    public static MapPosition Ahead(MapPosition p, int direction)
    {
        var offset = ExtractionPlanner.Rotate(new(0, -1), direction);
        return new(p.X + offset.X, p.Y + offset.Y);
    }
    private static double Distance(MapPosition a, MapPosition b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    public static int Direction(MapPosition a, MapPosition b) => b.X > a.X ? 4 : b.X < a.X ? 12 : b.Y > a.Y ? 8 : 0;

    /// <summary>Flow reservations for new supply-line equipment, excluding the belt it continues.</summary>
    public static IReadOnlySet<MapPosition> Forbidden(SpatialSnapshot map, string? joined = null)
    {
        var cells = new HashSet<MapPosition>();
        foreach (var entity in map.Entities)
        {
            if (map.Prototypes[entity.Name].Type is "transport-belt" or "underground-belt" or "splitter" && entity.Id != joined)
            {
                var near = new WorldBox(new(entity.Bounds.Min.X - 1, entity.Bounds.Min.Y - 1),
                    new(entity.Bounds.Max.X + 1, entity.Bounds.Max.Y + 1));
                for (double x = Math.Floor(near.Min.X) + .5; x <= near.Max.X; x++)
                    for (double y = Math.Floor(near.Min.Y) + .5; y <= near.Max.Y; y++)
                        if (near.Contains(new MapPosition(x, y))) cells.Add(new(x, y));
            }
            if (entity.PickupPosition is { } pickup) cells.Add(Cell(pickup));
            if (entity.DropPosition is { } drop) cells.Add(Cell(drop));
        }
        return cells;
    }
}
