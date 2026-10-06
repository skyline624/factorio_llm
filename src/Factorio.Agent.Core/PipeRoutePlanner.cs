namespace Factorio.Agent.Core;

public enum PipeRouteStatus { Found, NoRouteInSnapshot, BudgetExceeded, InvalidEndpoints }
public sealed record FluidEndpoint(string EntityId, int BoxIndex, int PortIndex, MapPosition Position, MapPosition TargetPosition);
/// <summary>One placed route entity in source-to-target order: an ordinary pipe, or a pipe-to-ground with its native direction.</summary>
public sealed record PipePiece(MapPosition Position, int Direction, bool Underground);
public sealed record PipeRoutePlan(PipeRouteStatus Status, FluidEndpoint? Source, FluidEndpoint? Target,
    IReadOnlyList<MapPosition> Pipes, int ExpandedNodes, IReadOnlyList<PipePiece>? Pieces = null);

/// <summary>Routes ordinary pipes on observed tiles without joining unrelated fluid ports.</summary>
public sealed class PipeRoutePlanner
{
    /// <summary>
    /// With an underground item, the route may also hop under any ground between two pipe-to-ground endpoints within the
    /// native underground distance. Hops cost two entities whatever their length, so long sections and obstacles such as
    /// belts, walls, water or buildings stop requiring walls of surface pipes that block the character's paths.
    /// </summary>
    public PipeRoutePlan Find(SpatialSnapshot map, string pipeItem, string sourceId, string targetId,
        string fluid, int nodeBudget = 20000, CancellationToken cancellationToken = default, IReadOnlySet<int>? sourceBoxIndices = null,
        string? undergroundItem = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        EntityGeometry pipe = map.Prototypes[map.Items[pipeItem].EntityName];
        if (pipe.Type != "pipe" || pipe.TileWidth != 1 || pipe.TileHeight != 1 || pipe.FluidBoxes?.Count != 1)
            throw new InvalidDataException("Routing requires native ordinary one-tile pipe geometry.");
        if (undergroundItem is not null)
            return new UndergroundPipeRouter(map, pipe, Tunnel(map, undergroundItem), fluid, cancellationToken)
                .Find(sourceId, targetId, nodeBudget, sourceBoxIndices);
        var source = map.Entities.Single(e => e.Id == sourceId);
        var target = map.Entities.Single(e => e.Id == targetId);
        var pairs = (from output in Ports(source, true).Where(p => sourceBoxIndices is null || sourceBoxIndices.Contains(p.BoxIndex))
                     from input in Ports(target, false)
                     select (Source: Endpoint(source, output), Target: Endpoint(target, input), Output: output, Input: input))
            .OrderBy(p => Manhattan(p.Source.TargetPosition, p.Target.TargetPosition)).ToArray();
        if (pairs.Length == 0) return new(PipeRouteStatus.InvalidEndpoints, null, null, [], 0);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        // Ordinary pipes use half-tile centers. Index native port targets once instead of
        // scanning the whole growing network for every A* neighbor.
        var portTargets = map.Entities.SelectMany(e => (e.FluidConnections ?? []).Select(p => (Entity: e, Port: p)))
            .ToLookup(p => Center(p.Port.TargetPosition));
        var placement = new Dictionary<MapPosition, bool>();
        int expanded = 0;
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FluidNetwork.IsConnected(map, pair.Source, pair.Target, fluid))
                return new(PipeRouteStatus.Found, pair.Source, pair.Target, [], expanded);
            if (pair.Source.TargetPosition == pair.Target.Position && pair.Target.TargetPosition == pair.Source.Position)
                return new(PipeRouteStatus.Found, pair.Source, pair.Target, [], expanded);
            if (pair.Output.TargetEntityId is not null || pair.Input.TargetEntityId is not null) continue;
            var start = pair.Source.TargetPosition;
            var goal = pair.Target.TargetPosition;
            var safe = new Dictionary<MapPosition, bool>();
            if (!Safe(start) || !Safe(goal)) continue;
            var frontier = new PriorityQueue<MapPosition, (int Cost, int Sequence)>();
            var cost = new Dictionary<MapPosition, int> { [start] = 0 };
            var previous = new Dictionary<MapPosition, MapPosition>();
            var closed = new HashSet<MapPosition>();
            int sequence = 0;
            frontier.Enqueue(start, (Manhattan(start, goal), sequence++));
            while (frontier.TryDequeue(out var current, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            bool Safe(MapPosition position)
            {
                if (!Aligned(position.X) || !Aligned(position.Y)) return false;
                if (safe.TryGetValue(position, out bool cached)) return cached;
                if (!placement.TryGetValue(position, out bool clear))
                    placement[position] = clear = field.PlacementClear(pipe, position, 0);
                return safe[position] = clear && !portTargets[Center(position)].Any(p =>
                    p.Port.TargetPosition.DistanceTo(position) < .01
                    && !(Matches(p.Entity, p.Port, pair.Source) || Matches(p.Entity, p.Port, pair.Target)))
                    && !(target.FluidConnections ?? []).Any(p => p.BoxIndex != pair.Target.BoxIndex
                        && p.TargetEntityId is null && p.Type == "normal" && p.FlowDirection is "input" or "input-output"
                        && Math.Abs(p.TargetPosition.X - position.X) + Math.Abs(p.TargetPosition.Y - position.Y) <= 1.01);
            }
        }
        return new(PipeRouteStatus.NoRouteInSnapshot, null, null, [], expanded);

        IEnumerable<ObservedFluidConnection> Ports(SpatialEntity entity, bool output) => (entity.FluidConnections ?? [])
            .Where(p => p.Type == "normal" && (p.Filter is null || p.Filter == fluid)
                && (p.FlowDirection == "input-output" || p.FlowDirection == (output ? "output" : "input")));
    }

    public static bool ConnectionsSafe(SpatialSnapshot map, MapPosition cell, FluidEndpoint source, FluidEndpoint target,
        IReadOnlySet<string> installedPipes) => !map.Entities.Any(e => !installedPipes.Contains(e.Id)
            && (e.FluidConnections ?? []).Any(p => p.TargetPosition.DistanceTo(cell) < .01
                && !(Matches(e, p, source) || Matches(e, p, target))))
        && !map.Entities.Where(e => e.Id == target.EntityId).SelectMany(e => e.FluidConnections ?? [])
            .Any(p => p.BoxIndex != target.BoxIndex && p.TargetEntityId is null && p.Type == "normal"
                && p.FlowDirection is "input" or "input-output"
                && Math.Abs(p.TargetPosition.X - cell.X) + Math.Abs(p.TargetPosition.Y - cell.Y) <= 1.01);

    internal static bool Matches(SpatialEntity entity, ObservedFluidConnection port, FluidEndpoint endpoint) =>
        entity.Id == endpoint.EntityId && port.BoxIndex == endpoint.BoxIndex && port.PortIndex == endpoint.PortIndex
        && port.Position == endpoint.Position && port.TargetPosition == endpoint.TargetPosition;
    internal static FluidEndpoint Endpoint(SpatialEntity entity, ObservedFluidConnection port) =>
        new(entity.Id, port.BoxIndex, port.PortIndex, port.Position, port.TargetPosition);
    internal static bool Aligned(double value) => double.IsFinite(value) && Math.Abs(value - .5 - Math.Round(value - .5)) < 1e-8;
    internal static MapPosition Center(MapPosition position) => new(Math.Round(position.X - .5) + .5, Math.Round(position.Y - .5) + .5);
    internal static int Manhattan(MapPosition a, MapPosition b) => checked((int)Math.Ceiling(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y)));
    private static IEnumerable<MapPosition> Neighbors(MapPosition p) => [new(p.X + 1, p.Y), new(p.X, p.Y + 1), new(p.X - 1, p.Y), new(p.X, p.Y - 1)];

    /// <summary>Native pipe-to-ground geometry: one open port and one opposite underground port with its reach.</summary>
    internal sealed record TunnelGeometry(EntityGeometry Entity, int OpenDirection, int UndergroundDirection, int Reach);

    internal static TunnelGeometry Tunnel(SpatialSnapshot map, string undergroundItem)
    {
        var entity = map.Prototypes[map.Items[undergroundItem].EntityName];
        var ports = entity.FluidBoxes is [var box] ? box.Connections : [];
        var open = ports.Where(p => p.Type == "normal").ToArray();
        var under = ports.Where(p => p.Type == "underground").ToArray();
        if (entity.Type != "pipe-to-ground" || entity.TileWidth != 1 || entity.TileHeight != 1 || open.Length != 1 || under.Length != 1
            || open[0].Direction % 4 != 0 || (open[0].Direction + 8) % 16 != under[0].Direction
            || under[0].MaxUndergroundDistance is not (>= 2 and <= 64)
            || ports.Any(p => p.Positions.Any(position => position != new MapPosition(0, 0))))
            throw new InvalidDataException("Underground routing requires native one-tile pipe-to-ground geometry with an opposite reach.");
        return new(entity, open[0].Direction, under[0].Direction, under[0].MaxUndergroundDistance!.Value);
    }
}

/// <summary>
/// A* over (tile, state): an ordinary pipe on the tile, or a pipe-to-ground exit whose open side faces a direction.
/// An exit must be continued straight ahead; turning always uses an ordinary pipe. A hop never passes over a foreign
/// pipe-to-ground on its axis, which could capture its native underground connection.
/// </summary>
internal sealed class UndergroundPipeRouter(SpatialSnapshot map, EntityGeometry pipe, PipeRoutePlanner.TunnelGeometry tunnel,
    string fluid, CancellationToken token)
{
    private const int EntityCost = 10;
    private static readonly int[] Directions = [0, 4, 8, 12];
    private readonly SpatialCollisionField field = new(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
    private readonly ILookup<MapPosition, (SpatialEntity Entity, ObservedFluidConnection Port)> portTargets = map.Entities
        .SelectMany(e => (e.FluidConnections ?? []).Select(p => (Entity: e, Port: p))).ToLookup(p => PipeRoutePlanner.Center(p.Port.TargetPosition));
    private readonly SpatialEntity[] foreignTunnels = map.Entities.Where(e => map.Prototypes.TryGetValue(e.Name, out var g) && g.Type == "pipe-to-ground").ToArray();

    private readonly record struct Node(MapPosition Tile, int Facing);

    public PipeRoutePlan Find(string sourceId, string targetId, int nodeBudget, IReadOnlySet<int>? sourceBoxIndices)
    {
        var source = map.Entities.Single(e => e.Id == sourceId);
        var target = map.Entities.Single(e => e.Id == targetId);
        var pairs = (from output in Ports(source, true).Where(p => sourceBoxIndices is null || sourceBoxIndices.Contains(p.BoxIndex))
                     from input in Ports(target, false)
                     select (Source: PipeRoutePlanner.Endpoint(source, output), Target: PipeRoutePlanner.Endpoint(target, input),
                         Output: output, Input: input))
            .OrderBy(p => PipeRoutePlanner.Manhattan(p.Source.TargetPosition, p.Target.TargetPosition)).ToArray();
        if (pairs.Length == 0) return new(PipeRouteStatus.InvalidEndpoints, null, null, [], 0);
        int expanded = 0;
        foreach (var pair in pairs)
        {
            token.ThrowIfCancellationRequested();
            if (FluidNetwork.IsConnected(map, pair.Source, pair.Target, fluid)
                || pair.Source.TargetPosition == pair.Target.Position && pair.Target.TargetPosition == pair.Source.Position)
                return new(PipeRouteStatus.Found, pair.Source, pair.Target, [], expanded, []);
            if (pair.Output.TargetEntityId is not null || pair.Input.TargetEntityId is not null) continue;
            var result = Search(pair.Source, pair.Target, target, nodeBudget - expanded, out int used);
            expanded += used;
            if (result is not null) return result;
            if (expanded >= nodeBudget) return new(PipeRouteStatus.BudgetExceeded, pair.Source, pair.Target, [], expanded);
        }
        return new(PipeRouteStatus.NoRouteInSnapshot, null, null, [], expanded);
    }

    private PipeRoutePlan? Search(FluidEndpoint from, FluidEndpoint to, SpatialEntity target, int budget, out int expanded)
    {
        expanded = 0;
        var start = from.TargetPosition;
        var goal = to.TargetPosition;
        var cost = new Dictionary<Node, int>();
        var previous = new Dictionary<Node, (Node? Parent, PipePiece[] Pieces)>();
        var frontier = new PriorityQueue<Node, (int, int)>();
        var closed = new HashSet<Node>();
        int sequence = 0;
        void Push(Node node, Node? parent, int g, PipePiece[] pieces)
        {
            if (cost.TryGetValue(node, out int old) && old <= g) return;
            cost[node] = g;
            previous[node] = (parent, pieces);
            frontier.Enqueue(node, (g + 2 * PipeRoutePlanner.Manhattan(node.Tile, goal), sequence++));
        }
        // An ordinary first pipe, or an entrance whose open side faces the source port itself.
        if (SafePipe(start, from, to, target)) Push(new(start, -1), null, EntityCost + 1, [new(start, 0, false)]);
        if (Toward(start, from.Position) is { } back)
            foreach (var (exit, pieces, g) in Hops(start, (back + 8) % 16, from, to))
                Push(exit, null, g, pieces);
        while (frontier.TryDequeue(out var current, out _))
        {
            token.ThrowIfCancellationRequested();
            if (!closed.Add(current)) continue;
            if (++expanded > budget) return null;
            if (current.Tile == goal && (current.Facing < 0 || Step(goal, current.Facing) == to.Position))
            {
                var path = new List<PipePiece>();
                for (Node? node = current; node is { } n; node = previous[n].Parent) path.InsertRange(0, previous[n].Pieces);
                return new(PipeRouteStatus.Found, from, to, path.Where(p => !p.Underground).Select(p => p.Position).ToArray(),
                    expanded, path.AsReadOnly());
            }
            int g0 = cost[current];
            foreach (int direction in current.Facing < 0 ? Directions : [current.Facing])
            {
                var next = Step(current.Tile, direction);
                if (SafePipe(next, from, to, target)) Push(new(next, -1), current, g0 + EntityCost + 1, [new(next, 0, false)]);
                foreach (var (exit, pieces, g) in Hops(next, direction, from, to))
                    Push(exit, current, g0 + g, pieces);
            }
        }
        return null;
    }

    /// <summary>Entrance on the tile, exits up to the native reach ahead; every endpoint is a clear, unclaimed tile.</summary>
    private IEnumerable<(Node Exit, PipePiece[] Pieces, int Cost)> Hops(MapPosition entrance, int direction, FluidEndpoint from, FluidEndpoint to)
    {
        if (!SafeTunnel(entrance, from, to)) yield break;
        var entrancePiece = new PipePiece(entrance, Mod(direction - tunnel.UndergroundDirection), true);
        for (int length = 2; length <= tunnel.Reach; length++)
        {
            var exit = Step(entrance, direction, length);
            if (Blocks(entrance, exit, direction)) yield break;
            if (!SafeTunnel(exit, from, to)) continue;
            yield return (new(exit, direction), [entrancePiece, new(exit, Mod(direction - tunnel.OpenDirection), true)],
                2 * EntityCost + length + 1);
        }
    }

    private bool Blocks(MapPosition entrance, MapPosition exit, int direction) => foreignTunnels.Any(e => e.Direction % 8 == direction % 8
        && (direction % 8 == 0 ? Math.Abs(e.Position.X - entrance.X) < .01 && Between(e.Position.Y, entrance.Y, exit.Y)
            : Math.Abs(e.Position.Y - entrance.Y) < .01 && Between(e.Position.X, entrance.X, exit.X)));

    private static bool Between(double value, double a, double b) => value > Math.Min(a, b) + .01 && value < Math.Max(a, b) - .01;

    private bool SafePipe(MapPosition position, FluidEndpoint from, FluidEndpoint to, SpatialEntity target) =>
        Unclaimed(position, pipe, from, to)
        && !(target.FluidConnections ?? []).Any(p => p.BoxIndex != to.BoxIndex && p.TargetEntityId is null && p.Type == "normal"
            && p.FlowDirection is "input" or "input-output"
            && Math.Abs(p.TargetPosition.X - position.X) + Math.Abs(p.TargetPosition.Y - position.Y) <= 1.01);

    private bool SafeTunnel(MapPosition position, FluidEndpoint from, FluidEndpoint to) => Unclaimed(position, tunnel.Entity, from, to);

    private readonly Dictionary<(MapPosition, string), bool> clear = new();
    private bool Unclaimed(MapPosition position, EntityGeometry geometry, FluidEndpoint from, FluidEndpoint to)
    {
        if (!PipeRoutePlanner.Aligned(position.X) || !PipeRoutePlanner.Aligned(position.Y)) return false;
        if (!clear.TryGetValue((position, geometry.Name), out bool free))
            clear[(position, geometry.Name)] = free = field.PlacementClear(geometry, position, 0);
        return free && !portTargets[PipeRoutePlanner.Center(position)].Any(p => p.Port.TargetPosition.DistanceTo(position) < .01
            && !(PipeRoutePlanner.Matches(p.Entity, p.Port, from) || PipeRoutePlanner.Matches(p.Entity, p.Port, to)));
    }

    private IEnumerable<ObservedFluidConnection> Ports(SpatialEntity entity, bool output) => (entity.FluidConnections ?? [])
        .Where(p => p.Type == "normal" && (p.Filter is null || p.Filter == fluid)
            && (p.FlowDirection == "input-output" || p.FlowDirection == (output ? "output" : "input")));

    private static int? Toward(MapPosition from, MapPosition to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        return (Math.Abs(dx), Math.Abs(dy)) switch
        {
            (< .01, > .99 and < 1.01) => dy < 0 ? 0 : 8,
            (> .99 and < 1.01, < .01) => dx > 0 ? 4 : 12,
            _ => null
        };
    }

    private static MapPosition Step(MapPosition p, int direction, int length = 1) => direction switch
    {
        0 => new(p.X, p.Y - length), 4 => new(p.X + length, p.Y), 8 => new(p.X, p.Y + length), 12 => new(p.X - length, p.Y),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static int Mod(int direction) => ((direction % 16) + 16) % 16;
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
            // Native underground ports target the next tile under ground, so a pipe-to-ground pair is matched by mutual identity.
            foreach (var port in entity.FluidConnections ?? [])
                if (port.BoxIndex == current.Box && port.TargetEntityId is { } next && port.TargetBoxIndex is { } box
                    && port.Type is "normal" or "underground" && port.FlowDirection is "output" or "input-output" && (port.Filter is null || port.Filter == fluid)
                    && entities.TryGetValue(next, out var neighbor) && (neighbor.FluidConnections ?? []).Any(back => back.BoxIndex == box
                        && back.TargetEntityId == current.EntityId && back.TargetBoxIndex == current.Box && back.Type == port.Type
                        && (port.Type == "underground" || back.Position == port.TargetPosition && back.TargetPosition == port.Position)
                        && (back.Filter is null || back.Filter == fluid) && back.FlowDirection is "input" or "input-output"))
                    queue.Enqueue((next, box));
        }
        return false;
    }
}
