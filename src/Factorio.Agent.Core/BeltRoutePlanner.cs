namespace Factorio.Agent.Core;

/// <summary>Partial: the target lies beyond the observation and the route ends near the observed edge, nearer the target.</summary>
public enum BeltRouteStatus { Found, NoRouteInSnapshot, BudgetExceeded, Partial }
public sealed record BeltRoutePlan(BeltRouteStatus Status, IReadOnlyList<PlacementCandidate> Belts, int ExpandedNodes);

public sealed class BeltRoutePlanner
{
    /// <summary>Tiles from the observed edge within which a partial route toward an unobserved target stops.</summary>
    public const int EdgeMargin = 6;

    /// <param name="joined">An existing belt the route continues: its neighbourhood stays usable, every other belt keeps its own free.</param>
    /// <param name="partial">
    /// With a target outside the observation, the route ends at the first reachable cell within <see cref="EdgeMargin"/> tiles of the
    /// observed edge that is at least that much nearer the target than the start; a long line is then routed in observed segments.
    /// </param>
    public BeltRoutePlan Find(SpatialSnapshot map, string beltItem, MapPosition start, MapPosition target,
        int nodeBudget = 12000, CancellationToken cancellationToken = default, string? joined = null, bool partial = false)
    {
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = map.Prototypes[map.Items[beltItem].EntityName];
        if (geometry.Type != "transport-belt" || geometry.TileWidth != 1 || geometry.TileHeight != 1 || geometry.BeltSpeed is not > 0)
            throw new InvalidDataException("Routing requires native one-tile ordinary belt geometry and speed.");
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var forbidden = Forbidden(map, joined);
        bool Clear(MapPosition p) => Cell(p) == p && !forbidden.Contains(p) && field.PlacementClear(geometry, p, 0);
        bool beyond = partial && !map.Bounds.Contains(target);
        if (!Clear(start) || !beyond && !Clear(target)) return new(BeltRouteStatus.NoRouteInSnapshot, [], 0);
        double startDistance = Distance(start, target);
        bool Edge(MapPosition p) => beyond && Distance(p, target) <= startDistance - EdgeMargin
            && (p.X - map.Bounds.Min.X < EdgeMargin || map.Bounds.Max.X - p.X < EdgeMargin
                || p.Y - map.Bounds.Min.Y < EdgeMargin || map.Bounds.Max.Y - p.Y < EdgeMargin);
        var frontier = new PriorityQueue<MapPosition, (double Score, int Sequence)>();
        var costs = new Dictionary<MapPosition, int> { [start] = 0 };
        var previous = new Dictionary<MapPosition, MapPosition>();
        var closed = new HashSet<MapPosition>();
        int sequence = 0, expanded = 0;
        frontier.Enqueue(start, (startDistance, sequence++));
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
                return new(path[^1] == target ? BeltRouteStatus.Found : BeltRouteStatus.Partial, belts, expanded);
            }
            foreach (var next in new MapPosition[] { new(current.X + 1, current.Y), new(current.X, current.Y + 1), new(current.X - 1, current.Y), new(current.X, current.Y - 1) })
            {
                if (closed.Contains(next) || !Clear(next)) continue;
                int cost = costs[current] + 1;
                if (costs.TryGetValue(next, out int old) && old <= cost) continue;
                costs[next] = cost;
                previous[next] = current;
                frontier.Enqueue(next, (cost + Distance(next, target), sequence++));
            }
        }
        return new(BeltRouteStatus.NoRouteInSnapshot, [], expanded);
    }

    /// <summary>
    /// Tile centres a new belt may not take: the neighbourhood of every existing belt except the joined one, so lines never connect
    /// by accident, and the pickup and drop tiles of inserters and drills, which would alter the transported flow.
    /// </summary>
    public static IReadOnlySet<MapPosition> Forbidden(SpatialSnapshot map, string? joined = null)
    {
        var cells = new HashSet<MapPosition>();
        foreach (var e in map.Entities)
        {
            if (map.Prototypes[e.Name].Type is "transport-belt" or "underground-belt" or "splitter" && e.Id != joined)
            {
                var near = new WorldBox(new(e.Bounds.Min.X - 1, e.Bounds.Min.Y - 1), new(e.Bounds.Max.X + 1, e.Bounds.Max.Y + 1));
                for (double x = Math.Floor(near.Min.X) + .5; x <= near.Max.X; x++)
                    for (double y = Math.Floor(near.Min.Y) + .5; y <= near.Max.Y; y++)
                        if (near.Contains(new MapPosition(x, y))) cells.Add(new(x, y));
            }
            if (e.PickupPosition is not null) cells.Add(Cell(e.PickupPosition));
            if (e.DropPosition is not null) cells.Add(Cell(e.DropPosition));
        }
        return cells;
    }

    public static MapPosition Cell(MapPosition position) => new(Math.Floor(position.X) + .5, Math.Floor(position.Y) + .5);
    /// <summary>The native belt direction from one tile toward an adjacent one.</summary>
    public static int Direction(MapPosition a, MapPosition b) => b.X > a.X ? 4 : b.X < a.X ? 12 : b.Y > a.Y ? 8 : 0;
    /// <summary>The tile a belt at this position feeds, one step along its native direction.</summary>
    public static MapPosition Ahead(MapPosition position, int direction)
    {
        var step = ExtractionPlanner.Rotate(new(0, -1), direction);
        return new(position.X + step.X, position.Y + step.Y);
    }
    private static double Distance(MapPosition a, MapPosition b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
