using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RouteStatus>))]
public enum RouteStatus { Found, GoalOutsideSnapshot, StartBlocked, NoRouteOnKnownGrid, BudgetExceeded }
public sealed record RoutePlan(RouteStatus Status, IReadOnlyList<MapPosition> Waypoints, int ExpandedNodes, double Length,
    bool UsesTightStartConnector = false);

/// <summary>Bounded A* on a half-tile grid, with continuous swept-box collision checks and line smoothing.</summary>
public sealed class RoutePlanner
{
    private readonly record struct Cell(int X, int Y)
    {
        public MapPosition Position => new(X / 2.0, Y / 2.0);
    }
    private readonly record struct SearchNode(Cell Cell, double Cost);
    private static readonly (int X, int Y)[] Neighbors = [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)];

    public RoutePlan Find(SpatialCollisionField field, MapPosition destination, double goalRadius = 0.2,
        int maximumNodes = 25000, TimeSpan? timeBudget = null, CancellationToken token = default)
    {
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Y) || goalRadius < 0
            || !double.IsFinite(goalRadius) || maximumNodes < 1) throw new ArgumentOutOfRangeException(nameof(destination));
        MapPosition start = field.Map.Actor.Position;
        RoutePlan Failure(RouteStatus status, int count = 0) => new(status, Array.Empty<MapPosition>(), count, 0);
        if (!field.Map.Bounds.Contains(destination)) return Failure(RouteStatus.GoalOutsideSnapshot);
        bool tightStart = !field.Walkable(start);
        if (tightStart && !field.Walkable(start, 0)) return Failure(RouteStatus.StartBlocked);
        if (start.DistanceTo(destination) <= goalRadius) return new(RouteStatus.Found, Array.Empty<MapPosition>(), 0, 0);
        if (field.SegmentClear(start, destination)) return new(RouteStatus.Found, Array.AsReadOnly(new[] { destination }), 0, start.DistanceTo(destination));
        var frontier = new PriorityQueue<SearchNode, (double Score, double Heuristic, long Sequence)>();
        var costs = new Dictionary<Cell, double>();
        var parents = new Dictionary<Cell, Cell>();
        long sequence = 0;
        double Heuristic(MapPosition p) => Math.Max(0, p.DistanceTo(destination) - goalRadius);
        // A collision stop can leave the actor within our extra steering margin. Escape locally using
        // the native body, then restore the normal margin for all subsequent edges.
        int escapeCells = tightStart ? 4 : 0;
        for (int x = (int)Math.Floor(start.X * 2) - escapeCells; x <= Math.Ceiling(start.X * 2) + escapeCells; x++)
            for (int y = (int)Math.Floor(start.Y * 2) - escapeCells; y <= Math.Ceiling(start.Y * 2) + escapeCells; y++)
            {
                var cell = new Cell(x, y);
                if (!field.Walkable(cell.Position) || !field.SegmentClear(start, cell.Position, tightStart ? 0 : 0.18)) continue;
                double cost = start.DistanceTo(cell.Position), h = Heuristic(cell.Position);
                costs[cell] = cost;
                frontier.Enqueue(new(cell, cost), (cost + h, h, sequence++));
            }
        int expanded = 0;
        long began = Stopwatch.GetTimestamp();
        TimeSpan budget = timeBudget ?? TimeSpan.FromMilliseconds(250);
        while (frontier.TryDequeue(out SearchNode node, out _))
        {
            token.ThrowIfCancellationRequested();
            if (node.Cost > costs[node.Cell] + 1e-9) continue;
            if (++expanded > maximumNodes || Stopwatch.GetElapsedTime(began) > budget)
                return Failure(RouteStatus.BudgetExceeded, expanded);
            MapPosition position = node.Cell.Position;
            bool atGoal = position.DistanceTo(destination) <= goalRadius;
            if (atGoal || field.SegmentClear(position, destination))
            {
                var reverse = new List<MapPosition> { position };
                Cell current = node.Cell;
                while (parents.TryGetValue(current, out Cell parent)) { reverse.Add(parent.Position); current = parent; }
                reverse.Reverse();
                var path = new List<MapPosition> { start };
                path.AddRange(reverse.Where(p => p.DistanceTo(start) > 1e-9));
                if (!atGoal) path.Add(destination);
                var smoothed = new List<MapPosition>();
                int index = 0;
                if (tightStart && path.Count > 1) { smoothed.Add(path[1]); index = 1; }
                while (index < path.Count - 1)
                {
                    int next = path.Count - 1;
                    while (next > index + 1 && !field.SegmentClear(path[index], path[next])) next--;
                    smoothed.Add(path[next]);
                    index = next;
                }
                double length = 0;
                MapPosition previous = start;
                foreach (MapPosition point in smoothed) { length += previous.DistanceTo(point); previous = point; }
                return new(RouteStatus.Found, smoothed.AsReadOnly(), expanded, length, tightStart);
            }
            foreach ((int dx, int dy) in Neighbors)
            {
                var next = new Cell(node.Cell.X + dx, node.Cell.Y + dy);
                if (!field.SegmentClear(position, next.Position)) continue;
                double cost = node.Cost + (dx == 0 || dy == 0 ? 0.5 : Math.Sqrt(0.5));
                if (costs.TryGetValue(next, out double existing) && cost >= existing - 1e-9) continue;
                costs[next] = cost;
                parents[next] = node.Cell;
                double h = Heuristic(next.Position);
                frontier.Enqueue(new(next, cost), (cost + h, h, sequence++));
            }
        }
        return Failure(RouteStatus.NoRouteOnKnownGrid, expanded);
    }
}
