namespace Factorio.Agent.Core;

public sealed record UpstreamTransportSegment(string SourceId, string TargetId, BeltTransportInstallation Installation);

/// <summary>Closes a single-producer material balance across replenished buffers and their incoming transport lines.</summary>
public sealed class BeltTransportBoundary
{
    private BeltTransportBoundary(string sourceId, MaterialEndpoint root, IReadOnlyList<MaterialEndpoint> buffers,
        IReadOnlyList<UpstreamTransportSegment> segments)
    {
        SourceId = sourceId;
        Root = root;
        Buffers = buffers;
        Segments = segments;
        ReservedEntityIds = segments.SelectMany(s => s.Installation.BeltIds
                .Append(s.Installation.SourceInserterId).Append(s.Installation.TargetInserterId))
            .Concat(buffers.Select(b => b.EntityId)).Append(root.EntityId).ToHashSet(StringComparer.Ordinal);
    }

    public string SourceId { get; }
    public MaterialEndpoint Root { get; }
    public IReadOnlyList<MaterialEndpoint> Buffers { get; }
    public IReadOnlyList<UpstreamTransportSegment> Segments { get; }
    public IReadOnlySet<string> ReservedEntityIds { get; }

    public static BeltTransportBoundary From(SpatialSnapshot map, FactorySnapshot stock, ProductionCatalog catalog,
        string sourceId, string targetId, string item)
    {
        if (map.Scope != stock.Scope || map.Scope != catalog.Scope) throw new InvalidDataException("Transport boundary scopes differ.");
        var buffers = new List<MaterialEndpoint>();
        var segments = new List<UpstreamTransportSegment>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { targetId };
        string current = sourceId;
        MaterialEndpoint root;
        while (true)
        {
            if (!visited.Add(current) || visited.Count > 33) throw new InvalidDataException("A transport boundary contains a cycle or exceeds 32 endpoints.");
            var endpoint = MaterialEndpoint.From(stock, catalog, current, item, true);
            endpoint.Read(stock, item);
            var feeders = map.Entities.Where(e => e.DropTargetId == current).ToArray();
            if (endpoint.Recipe is not null || feeders.Length == 0) { root = endpoint; break; }
            if (feeders.Length != 1) throw new InvalidDataException("A replenished buffer requires exactly one verified upstream line.");
            string? beltId = feeders[0].PickupTargetId;
            var belts = new HashSet<string>(StringComparer.Ordinal);
            SpatialEntity first;
            while (true)
            {
                first = map.Entities.SingleOrDefault(e => e.Id == beltId)
                    ?? throw new InvalidDataException("An upstream feeder has no observed belt source.");
                if (map.Prototypes[first.Name].Type != "transport-belt" || first.BeltConnections is null
                    || !belts.Add(first.Id) || belts.Count > 200)
                    throw new InvalidDataException("An upstream route is incomplete, unsupported or cyclic.");
                if (first.BeltConnections.Inputs.Count == 0) break;
                if (first.BeltConnections.Inputs.Count != 1) throw new InvalidDataException("An upstream route has multiple inputs.");
                beltId = first.BeltConnections.Inputs[0];
            }
            var extractors = map.Entities.Where(e => e.DropTargetId == first.Id).ToArray();
            if (extractors.Length != 1 || extractors[0].PickupTargetId is not { } upstreamId)
                throw new InvalidDataException("An upstream route needs one observed source extractor.");
            var line = BeltTransportNetwork.FindSegment(map, upstreamId, current)
                ?? throw new InvalidDataException("The upstream line does not connect its source to the buffer.");
            if (line.TargetInserterId != feeders[0].Id || segments.Any(s => s.Installation.BeltIds.Intersect(line.BeltIds).Any()))
                throw new InvalidDataException("The upstream boundary overlaps another segment.");
            buffers.Add(endpoint);
            segments.Add(new(upstreamId, current, line));
            current = upstreamId;
        }
        var boundary = new BeltTransportBoundary(sourceId, root, buffers, segments);
        boundary.Verify(map);
        return boundary;
    }

    public void Verify(SpatialSnapshot map)
    {
        if (Root.Recipe is null && map.Entities.Any(e => e.DropTargetId == Root.EntityId))
            throw new InvalidDataException("The finite root stock acquired an unaccounted incoming flow.");
        foreach (var segment in Segments)
        {
            var feeders = map.Entities.Where(e => e.DropTargetId == segment.TargetId).ToArray();
            if (feeders.Length != 1 || feeders[0].Id != segment.Installation.TargetInserterId)
                throw new InvalidDataException("The incoming buffer flow changed after its baseline.");
            var line = segment.Installation;
            BeltTransportNetwork.VerifySegment(map, segment.SourceId, segment.TargetId, line.SourceInserterId, line.TargetInserterId, line.BeltIds);
        }
    }

    public BeltTransportInstallation? FindConnection(SpatialSnapshot map, string targetId)
    {
        Verify(map);
        return BeltTransportNetwork.FindSegment(map, SourceId, targetId);
    }

    public void VerifyConnection(SpatialSnapshot map, string targetId, BeltTransportInstallation line)
    {
        Verify(map);
        BeltTransportNetwork.VerifySegment(map, SourceId, targetId, line.SourceInserterId, line.TargetInserterId, line.BeltIds);
    }

    public BeltTransportReading Read(FactorySnapshot snapshot, MaterialEndpoint target, string item,
        IReadOnlyList<string> downstreamBelts, IReadOnlyList<string> downstreamInserters)
    {
        string[] belts = Segments.SelectMany(s => s.Installation.BeltIds).Concat(downstreamBelts).ToArray();
        string[] arms = Segments.SelectMany(s => new[] { s.Installation.SourceInserterId, s.Installation.TargetInserterId })
            .Concat(downstreamInserters).ToArray();
        if (belts.Concat(arms).Distinct(StringComparer.Ordinal).Count() != belts.Length + arms.Length)
            throw new InvalidDataException("The transport boundary would count an entity more than once.");
        var reading = BeltTransportReading.From(snapshot, Root, target, item, belts, arms);
        long held = 0;
        foreach (var buffer in Buffers) held = checked(held + buffer.Read(snapshot, item).Count);
        return reading with { Transit = checked(reading.Transit + held) };
    }
}
