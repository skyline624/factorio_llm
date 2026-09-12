namespace Factorio.Agent.Core;

public enum PipeRouteStatus { Found, NoRouteInSnapshot, BudgetExceeded, InvalidEndpoints }
public sealed record FluidEndpoint(string EntityId, int BoxIndex, int PortIndex, MapPosition Position, MapPosition TargetPosition);
public sealed record PipeRoutePlan(PipeRouteStatus Status, FluidEndpoint? Source, FluidEndpoint? Target,
    IReadOnlyList<MapPosition> Pipes, int ExpandedNodes);

/// <summary>Routes ordinary pipes on observed tiles without joining unrelated fluid ports.</summary>
public sealed class PipeRoutePlanner
{
    public PipeRoutePlan Find(SpatialSnapshot map, string pipeItem, string sourceId, string targetId,
        string fluid, int nodeBudget = 20000)
    {
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        EntityGeometry pipe = map.Prototypes[map.Items[pipeItem].EntityName];
        if (pipe.Type != "pipe" || pipe.TileWidth != 1 || pipe.TileHeight != 1 || pipe.FluidBoxes?.Count != 1)
            throw new InvalidDataException("Routing requires native ordinary one-tile pipe geometry.");
        var source = map.Entities.Single(e => e.Id == sourceId);
        var target = map.Entities.Single(e => e.Id == targetId);
        var pairs = (from output in Ports(source, true)
                     from input in Ports(target, false)
                     select (Source: Endpoint(source, output), Target: Endpoint(target, input), Output: output, Input: input))
            .OrderBy(p => Manhattan(p.Source.TargetPosition, p.Target.TargetPosition)).ToArray();
        if (pairs.Length == 0) return new(PipeRouteStatus.InvalidEndpoints, null, null, [], 0);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        int expanded = 0;
        foreach (var pair in pairs)
        {
            if (FluidNetwork.IsConnected(map, pair.Source, pair.Target, fluid))
                return new(PipeRouteStatus.Found, pair.Source, pair.Target, [], expanded);
            if (pair.Source.TargetPosition == pair.Target.Position && pair.Target.TargetPosition == pair.Source.Position)
                return new(PipeRouteStatus.Found, pair.Source, pair.Target, [], expanded);
            if (pair.Output.TargetEntityId is not null || pair.Input.TargetEntityId is not null) continue;
            var start = pair.Source.TargetPosition;
            var goal = pair.Target.TargetPosition;
            if (!Safe(start) || !Safe(goal)) continue;
            var frontier = new PriorityQueue<MapPosition, (int Cost, int Sequence)>();
            var cost = new Dictionary<MapPosition, int> { [start] = 0 };
            var previous = new Dictionary<MapPosition, MapPosition>();
            var closed = new HashSet<MapPosition>();
            int sequence = 0;
            frontier.Enqueue(start, (Manhattan(start, goal), sequence++));
            while (frontier.TryDequeue(out var current, out _))
            {
                if (!closed.Add(current)) continue;
                if (++expanded > nodeBudget) return new(PipeRouteStatus.BudgetExceeded, pair.Source, pair.Target, [], expanded - 1);
                if (current == goal)
                {
                    var path = new List<MapPosition> { current };
                    while (previous.TryGetValue(current, out var parent)) { path.Add(parent); current = parent; }
                    path.Reverse();
                    return new(PipeRouteStatus.Found, pair.Source, pair.Target, path, expanded);
                }
                foreach (var next in Neighbors(current))
                {
                    if (closed.Contains(next) || !Safe(next)) continue;
                    int candidate = cost[current] + 1;
                    if (cost.TryGetValue(next, out var old) && old <= candidate) continue;
                    cost[next] = candidate;
                    previous[next] = current;
                    frontier.Enqueue(next, (candidate + Manhattan(next, goal), sequence++));
                }
            }
            bool Safe(MapPosition position) => Aligned(position.X) && Aligned(position.Y)
                && field.PlacementClear(pipe, position, 0) && ConnectionsSafe(map, position, pair.Source, pair.Target, new HashSet<string>());
        }
        return new(PipeRouteStatus.NoRouteInSnapshot, null, null, [], expanded);

        IEnumerable<ObservedFluidConnection> Ports(SpatialEntity entity, bool output) => (entity.FluidConnections ?? [])
            .Where(p => p.Type == "normal" && (p.Filter is null || p.Filter == fluid)
                && (p.FlowDirection == "input-output" || p.FlowDirection == (output ? "output" : "input")));
    }

    public static bool ConnectionsSafe(SpatialSnapshot map, MapPosition cell, FluidEndpoint source, FluidEndpoint target,
        IReadOnlySet<string> installedPipes) => !map.Entities.Any(e => !installedPipes.Contains(e.Id)
            && (e.FluidConnections ?? []).Any(p => p.TargetPosition.DistanceTo(cell) < .01
                && !(Matches(e, p, source) || Matches(e, p, target))));

    private static bool Matches(SpatialEntity entity, ObservedFluidConnection port, FluidEndpoint endpoint) =>
        entity.Id == endpoint.EntityId && port.BoxIndex == endpoint.BoxIndex && port.PortIndex == endpoint.PortIndex
        && port.Position == endpoint.Position && port.TargetPosition == endpoint.TargetPosition;
    private static FluidEndpoint Endpoint(SpatialEntity entity, ObservedFluidConnection port) =>
        new(entity.Id, port.BoxIndex, port.PortIndex, port.Position, port.TargetPosition);
    private static bool Aligned(double value) => double.IsFinite(value) && Math.Abs(value - .5 - Math.Round(value - .5)) < 1e-8;
    private static int Manhattan(MapPosition a, MapPosition b) => checked((int)Math.Ceiling(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y)));
    private static IEnumerable<MapPosition> Neighbors(MapPosition p) => [new(p.X + 1, p.Y), new(p.X, p.Y + 1), new(p.X - 1, p.Y), new(p.X, p.Y - 1)];
}

public static class FluidNetwork
{
    public static bool IsConnected(SpatialSnapshot map, FluidEndpoint source, FluidEndpoint target, string fluid)
    {
        var entities = map.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        bool ValidEndpoint(FluidEndpoint endpoint, bool output) => entities.TryGetValue(endpoint.EntityId, out var owner)
            && (owner.FluidConnections ?? []).Any(p => p.BoxIndex == endpoint.BoxIndex && p.PortIndex == endpoint.PortIndex
                && p.Position == endpoint.Position && p.TargetPosition == endpoint.TargetPosition && p.Type == "normal"
                && (p.Filter is null || p.Filter == fluid) && (p.FlowDirection == "input-output" || p.FlowDirection == (output ? "output" : "input")));
        if (!ValidEndpoint(source, true) || !ValidEndpoint(target, false)) return false;
        var seen = new HashSet<(string EntityId, int Box)>();
        var queue = new Queue<(string EntityId, int Box)>();
        queue.Enqueue((source.EntityId, source.BoxIndex));
        while (queue.TryDequeue(out var current))
        {
            if (!seen.Add(current) || !entities.TryGetValue(current.EntityId, out var entity)) continue;
            if (current == (target.EntityId, target.BoxIndex)) return true;
            foreach (var port in entity.FluidConnections ?? [])
                if (port.BoxIndex == current.Box && port.TargetEntityId is { } next && port.TargetBoxIndex is { } box
                    && port.Type == "normal" && port.FlowDirection is "output" or "input-output" && (port.Filter is null || port.Filter == fluid)
                    && entities.TryGetValue(next, out var neighbor) && (neighbor.FluidConnections ?? []).Any(back => back.BoxIndex == box
                        && back.TargetEntityId == current.EntityId && back.TargetBoxIndex == current.Box && back.Type == "normal"
                        && back.Position == port.TargetPosition && back.TargetPosition == port.Position
                        && (back.Filter is null || back.Filter == fluid) && back.FlowDirection is "input" or "input-output"))
                    queue.Enqueue((next, box));
        }
        return false;
    }
}
