namespace Factorio.Agent.Core;

public enum BeltRouteStatus { Found, NoRouteInSnapshot, BudgetExceeded }
public sealed record BeltRoutePlan(BeltRouteStatus Status, IReadOnlyList<PlacementCandidate> Belts, int ExpandedNodes);

public sealed class BeltRoutePlanner
{
    public BeltRoutePlan Find(SpatialSnapshot map, string beltItem, MapPosition start, MapPosition target,
        int nodeBudget = 12000, CancellationToken cancellationToken = default)
    {
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = map.Prototypes[map.Items[beltItem].EntityName];
        if (geometry.Type != "transport-belt" || geometry.TileWidth != 1 || geometry.TileHeight != 1 || geometry.BeltSpeed is not > 0)
            throw new InvalidDataException("Routing requires native one-tile ordinary belt geometry and speed.");
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        bool Clear(MapPosition p) => Cell(p) == p && field.PlacementClear(geometry, p, 0)
            && !map.Entities.Any(e =>
                (map.Prototypes[e.Name].Type is "transport-belt" or "underground-belt" or "splitter"
                    && new WorldBox(new(e.Bounds.Min.X - 1, e.Bounds.Min.Y - 1), new(e.Bounds.Max.X + 1, e.Bounds.Max.Y + 1)).Contains(p))
                || (e.PickupPosition is not null && Cell(e.PickupPosition) == p)
                || (e.DropPosition is not null && Cell(e.DropPosition) == p));
        if (!Clear(start) || !Clear(target)) return new(BeltRouteStatus.NoRouteInSnapshot, [], 0);
        var frontier = new PriorityQueue<MapPosition, (double Score, int Sequence)>();
        var costs = new Dictionary<MapPosition, int> { [start] = 0 };
        var previous = new Dictionary<MapPosition, MapPosition>();
        var closed = new HashSet<MapPosition>();
        int sequence = 0, expanded = 0;
        frontier.Enqueue(start, (Distance(start, target), sequence++));
        while (frontier.TryDequeue(out var current, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!closed.Add(current)) continue;
            if (++expanded > nodeBudget) return new(BeltRouteStatus.BudgetExceeded, [], expanded - 1);
            if (current == target)
            {
                var path = new List<MapPosition> { current };
                while (previous.TryGetValue(current, out var parent)) { path.Add(parent); current = parent; }
                path.Reverse();
                var belts = path.Select((p, i) => new PlacementCandidate(p,
                    path.Count == 1 ? 0 : Direction(i + 1 < path.Count ? p : path[i - 1], i + 1 < path.Count ? path[i + 1] : p), i)).ToArray();
                return new(BeltRouteStatus.Found, belts, expanded);
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

    public static MapPosition Cell(MapPosition position) => new(Math.Floor(position.X) + .5, Math.Floor(position.Y) + .5);
    private static double Distance(MapPosition a, MapPosition b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private static int Direction(MapPosition a, MapPosition b) => b.X > a.X ? 4 : b.X < a.X ? 12 : b.Y > a.Y ? 8 : 0;
}
