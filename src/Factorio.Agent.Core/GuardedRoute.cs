namespace Factorio.Agent.Core;

/// <summary>
/// Coarse detour of a long trip around remembered nest guards, searched on a grid of <see cref="Cell"/>-tile cells. Local
/// exploration steps only see 28 tiles ahead: on 2026-10-08 (seed 20261106) remembered worms and spawners formed an
/// overlapping wall across the way from the circuit zone to the oil field, no guarded step approached the oil field, and the
/// actor walked straight through the worms and died. Terrain is left to the local steps; only the guards are avoided here.
/// </summary>
public static class GuardedRoute
{
    public const double Cell = 16;
    /// <summary>How far beyond the trip's own box the detour may go.</summary>
    public const double Margin = 320;
    private const int MaximumNodes = 20000;

    /// <summary>
    /// The farthest point of the detour visible from the start, or <paramref name="to"/> when the straight way is clear. Guards
    /// holding the start or the destination are ignored: one may be left, the other cannot be avoided. Null when no detour exists
    /// within the margin.
    /// </summary>
    public static MapPosition? Waypoint(MapPosition from, MapPosition to, IReadOnlyList<(MapPosition Center, double Radius)> guards)
    {
        var active = guards.Where(g => g.Center.DistanceTo(to) > g.Radius && g.Center.DistanceTo(from) > g.Radius).ToArray();
        if (!Crosses(from, to, active)) return to;
        double minX = Math.Min(from.X, to.X) - Margin, minY = Math.Min(from.Y, to.Y) - Margin;
        int width = (int)Math.Ceiling((Math.Abs(from.X - to.X) + 2 * Margin) / Cell) + 1;
        int height = (int)Math.Ceiling((Math.Abs(from.Y - to.Y) + 2 * Margin) / Cell) + 1;
        MapPosition Center((int X, int Y) c) => new(minX + c.X * Cell, minY + c.Y * Cell);
        (int, int) CellOf(MapPosition p) => ((int)Math.Round((p.X - minX) / Cell), (int)Math.Round((p.Y - minY) / Cell));
        bool Free((int X, int Y) c) => c.X >= 0 && c.Y >= 0 && c.X < width && c.Y < height
            && active.All(g => Center(c).DistanceTo(g.Center) > g.Radius + Cell / 2);
        var start = CellOf(from);
        var goal = CellOf(to);
        var cost = new Dictionary<(int, int), double> { [start] = 0 };
        var previous = new Dictionary<(int, int), (int, int)>();
        var frontier = new PriorityQueue<(int X, int Y), double>();
        frontier.Enqueue(start, Center(start).DistanceTo(to));
        var closed = new HashSet<(int, int)>();
        while (frontier.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current)) continue;
            if (closed.Count > MaximumNodes) return null;
            if (current == goal)
            {
                var path = new List<MapPosition> { to };
                for (var node = current; previous.TryGetValue(node, out var parent); node = parent) path.Insert(0, Center(parent));
                // The farthest path point reachable in a straight line keeps the steps from zigzagging over grid cells.
                return path.LastOrDefault(p => !Crosses(from, p, active)) ?? path[0];
            }
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
            {
                var next = (current.X + dx, current.Y + dy);
                if (closed.Contains(next) || next != goal && !Free(next)) continue;
                double candidate = cost[current] + Cell * Math.Sqrt(dx * dx + dy * dy);
                if (cost.TryGetValue(next, out double old) && old <= candidate) continue;
                cost[next] = candidate;
                previous[next] = current;
                frontier.Enqueue(next, candidate + Center(next).DistanceTo(to));
            }
        }
        return null;
    }

    /// <summary>Whether the straight segment passes inside a guard.</summary>
    public static bool Crosses(MapPosition a, MapPosition b, IReadOnlyList<(MapPosition Center, double Radius)> guards) =>
        guards.Any(g => Distance(g.Center, a, b) < g.Radius);

    private static double Distance(MapPosition p, MapPosition a, MapPosition b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return p.DistanceTo(new(a.X + t * dx, a.Y + t * dy));
    }
}
