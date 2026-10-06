namespace Factorio.Agent.Core;

/// <summary>Computes physical surface belts and explicit underground pairs from observed native geometry.</summary>
public sealed class UndergroundBeltRoutePlanner
{
    private readonly record struct Node(MapPosition Position, int Incoming, bool TunnelExit);
    private readonly record struct Step(Node Parent, int Direction, bool Tunnel);

    public BeltRoutePlan Find(SpatialSnapshot map, string beltItem, string undergroundItem, MapPosition start,
        MapPosition target, int nodeBudget = 12000, CancellationToken token = default)
        => FindCore(map,beltItem,undergroundItem,start,target,nodeBudget,token,null);

    internal BeltRoutePlan FindWithField(SpatialCollisionField field,string beltItem,string undergroundItem,MapPosition start,
        MapPosition target,int nodeBudget,CancellationToken token) => FindCore(field.Map,beltItem,undergroundItem,start,target,nodeBudget,token,field);

    private BeltRoutePlan FindCore(SpatialSnapshot map,string beltItem,string undergroundItem,MapPosition start,MapPosition target,
        int nodeBudget,CancellationToken token,SpatialCollisionField? collisionField)
    {
        if (nodeBudget < 1) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        token.ThrowIfCancellationRequested();
        var belt = map.Prototypes[map.Items[beltItem].EntityName];
        var tunnel = map.Prototypes[map.Items[undergroundItem].EntityName];
        if (belt.Type != "transport-belt" || tunnel.Type != "underground-belt"
            || belt.TileWidth != 1 || belt.TileHeight != 1 || tunnel.TileWidth != 1 || tunnel.TileHeight != 1
            || belt.BeltSpeed is not > 0 || tunnel.BeltSpeed != belt.BeltSpeed || tunnel.MaxUndergroundDistance is not (>= 2 and <= 255))
            throw new InvalidDataException("Underground routing requires observed compatible one-tile belt prototypes and native range.");
        if (BeltRoutePlanner.Cell(start) != start || BeltRoutePlanner.Cell(target) != target)
            return new(BeltRouteStatus.NoRouteInSnapshot, [], 0);
        var collision = collisionField ?? new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var surface = new BeltRoutingField(map, belt, start, token,collisionField:collision);
        var conveyors = map.Entities.Where(e => map.Prototypes[e.Name].Type is "transport-belt" or "underground-belt" or "splitter").ToArray();
        var conveyorIds = conveyors.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var occupied = conveyors.Select(e => BeltRoutePlanner.Cell(e.Position)).ToHashSet();
        var incoming = conveyors.Where(e => map.Prototypes[e.Name].Type != "underground-belt" || e.Underground?.Type != "input")
            .Select(e => BeltRoutingField.Front(BeltRoutePlanner.Cell(e.Position), e.Direction)).ToHashSet();
        var ports = map.Entities.SelectMany(e => new[] { e.PickupPosition, e.DropPosition }).OfType<MapPosition>()
            .Select(BeltRoutePlanner.Cell).ToHashSet();
        var knownTiles = new HashSet<MapPosition>();
        foreach (var row in map.Rows.Where(row => row.Name != SurveyedTransportFrame.UnknownTile))
            for (int x = row.X; x < row.X + row.Length; x++) knownTiles.Add(new(x + .5, row.Y + .5));
        var endpoints = new Dictionary<(MapPosition, int, bool), bool>();
        var potential = new Dictionary<(MapPosition,int),bool>();
        bool NeedsCrossing(MapPosition p,int direction)
        {
            if (potential.TryGetValue((p,direction),out bool cached)) return cached;
            bool needed = !surface.SurfaceClear(p);
            for (int length = 1; !needed && length <= tunnel.MaxUndergroundDistance.Value; length++)
            {
                var point = BeltRoutingField.Front(p,direction,length);
                if (!knownTiles.Contains(point)) break;
                needed = !surface.SurfaceClear(point);
            }
            return potential[(p,direction)] = needed;
        }
        bool EndClear(MapPosition p, int direction, bool input)
        {
            var key = (p, direction, input);
            if (endpoints.TryGetValue(key, out bool cached)) return cached;
            bool allowed = TransportConstructionSafety.Allows(map, p)
                && collision.PlacementClear(tunnel, p, direction) && !ports.Contains(p)
                && !incoming.Contains(p) && (input || !occupied.Contains(BeltRoutingField.Front(p, direction)))
                && !conveyors.Any(e => map.Prototypes[e.Name].Type == "splitter"
                    && new WorldBox(new(e.Bounds.Min.X - 1, e.Bounds.Min.Y - 1), new(e.Bounds.Max.X + 1, e.Bounds.Max.Y + 1)).Contains(p))
                && !conveyors.Any(e => e.Name == tunnel.Name && e.Direction == direction
                    && Parallel(p, BeltRoutePlanner.Cell(e.Position), direction)
                    && Distance(p, e.Position) <= tunnel.MaxUndergroundDistance.Value
                    && (e.Underground is null || e.Underground.NeighbourCount > 0
                            && (e.Underground.NeighbourId is null || !conveyorIds.Contains(e.Underground.NeighbourId)) || (input
                        ? e.Underground.Type == "output" && ForwardDistance(p,e.Position,direction) > 0
                        : e.Underground.Type == "input" && ForwardDistance(e.Position,p,direction) > 0)));
            return endpoints[key] = allowed;
        }

        var root = new Node(start, -1, false);
        var queue = new PriorityQueue<Node, (double Score, double Remaining, int Sequence)>();
        var costs = new Dictionary<Node, int> { [root] = 0 };
        var previous = new Dictionary<Node, Step>();
        var closed = new HashSet<Node>();
        int expanded = 0, sequence = 0;
        queue.Enqueue(root, (Distance(start, target), Distance(start, target), sequence++));
        while (queue.TryDequeue(out var current, out _))
        {
            token.ThrowIfCancellationRequested();
            if (!closed.Add(current)) continue;
            if (++expanded > nodeBudget) return new(BeltRouteStatus.BudgetExceeded, [], expanded - 1);
            if (current.Position == target && (current.TunnelExit || surface.SurfaceClear(target)))
            {
                var steps = new List<(Node Node, Step Step)>();
                var cursor = current;
                while (previous.TryGetValue(cursor, out var step)) { steps.Add((cursor, step)); cursor = step.Parent; }
                steps.Reverse();
                var parts = new List<PlacementCandidate> { new(start, 0, 0) };
                foreach (var (next, step) in steps)
                {
                    parts[^1] = parts[^1] with { Direction = step.Direction, UndergroundType = step.Tunnel ? "input" : parts[^1].UndergroundType };
                    parts.Add(new(next.Position, step.Direction, parts.Count, next.TunnelExit ? "output" : null));
                }
                if (parts.Select(p => p.Position).Distinct().Count() != parts.Count || !PairsIsolated(parts))
                    return new(BeltRouteStatus.NoRouteInSnapshot, [], expanded);
                return new(BeltRouteStatus.Found, parts, expanded);
            }
            foreach (int direction in new[] { 0, 4, 8, 12 })
            {
                token.ThrowIfCancellationRequested();
                if ((!current.TunnelExit || direction == current.Incoming) && (current.TunnelExit || surface.SurfaceClear(current.Position)))
                {
                    var next = BeltRoutingField.Front(current.Position, direction);
                    // An ordinary belt may become the input of a subsequent tunnel.
                    if (surface.SurfaceClear(next) || EndClear(next, direction, true))
                        Enqueue(new(next, NeedsCrossing(next,direction) ? direction : -2, false), direction, false, 1);
                }
                if (current.TunnelExit || current.Incoming != -1 && current.Incoming != direction
                    || !NeedsCrossing(current.Position,direction) || !EndClear(current.Position, direction, true)) continue;
                bool obstruction = !surface.SurfaceClear(current.Position);
                for (int length = 2; length <= tunnel.MaxUndergroundDistance.Value; length++)
                {
                    var next = BeltRoutingField.Front(current.Position, direction, length);
                    if (!knownTiles.Contains(next)) break;
                    var buried = BeltRoutingField.Front(current.Position,direction,length-1);
                    if (!knownTiles.Contains(buried)) break;
                    obstruction |= !surface.SurfaceClear(buried);
                    if ((obstruction || !surface.SurfaceClear(next)) && EndClear(next, direction, false))
                        Enqueue(new(next, direction, true), direction, true, length + 2);
                }
            }

            void Enqueue(Node next, int direction, bool underground, int distance)
            {
                if (closed.Contains(next)) return;
                int cost = costs[current] + distance;
                if (costs.TryGetValue(next, out int old) && old <= cost) return;
                costs[next] = cost;
                previous[next] = new(current, direction, underground);
                double remaining = Distance(next.Position, target);
                queue.Enqueue(next, (cost + remaining, remaining, sequence++));
            }
        }
        return new(BeltRouteStatus.NoRouteInSnapshot, [], expanded);

        bool PairsIsolated(IReadOnlyList<PlacementCandidate> parts)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].UndergroundType != "input") continue;
                if (i + 1 >= parts.Count || parts[i + 1].UndergroundType != "output" || parts[i].Direction != parts[i + 1].Direction) return false;
                double span = ForwardDistance(parts[i].Position, parts[i + 1].Position, parts[i].Direction);
                if (!Parallel(parts[i].Position, parts[i + 1].Position, parts[i].Direction)
                    || span < 2 || span > tunnel.MaxUndergroundDistance.Value) return false;
                for (int j = 0; j < parts.Count; j++)
                    if (j != i && j != i + 1 && parts[j].UndergroundType is not null && parts[j].Direction == parts[i].Direction
                        && Parallel(parts[i].Position, parts[j].Position, parts[i].Direction))
                    {
                        // An output looks backward for an input; an input looks forward for an output.
                        // A following pair is safe even when its input is adjacent to this pair's output.
                        double competing = parts[j].UndergroundType == "output"
                            ? ForwardDistance(parts[i].Position, parts[j].Position, parts[i].Direction)
                            : ForwardDistance(parts[j].Position, parts[i + 1].Position, parts[i].Direction);
                        if (competing > 0 && competing <= span) return false;
                    }
            }
            return true;
        }
    }

    private static bool Parallel(MapPosition a, MapPosition b, int direction) => direction is 0 or 8 ? a.X == b.X : a.Y == b.Y;
    private static double ForwardDistance(MapPosition a, MapPosition b, int direction) => direction switch
    {
        0 => a.Y - b.Y,
        4 => b.X - a.X,
        8 => b.Y - a.Y,
        12 => a.X - b.X,
        _ => throw new InvalidDataException("Underground pairs require cardinal directions.")
    };
    private static double Distance(MapPosition a, MapPosition b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
