namespace Factorio.Agent.Core;

/// <summary>An enemy spawner or worm this actor has seen itself, with the tick of its last sighting.</summary>
public sealed record NestSighting(string Id, string Name, MapPosition Position, double? Range, long Tick);

/// <summary>Reads the actor's remembered enemy structures; the session client owns the durable file.</summary>
public interface INestMemoryReader
{
    Task<NestMemory> ReadNestMemoryAsync(ActorScope scope, CancellationToken token = default);
}

/// <summary>Structures within linking distance of each other: one enemy base as the actor remembers it.</summary>
public sealed record NestCluster(MapPosition Center, IReadOnlyList<NestSighting> Structures)
{
    public int Spawners => Structures.Count(s => s.Range is null);
    public int Worms => Structures.Count(s => s.Range is not null);
    public double Radius => Structures.Max(s => s.Position.DistanceTo(Center));
}

/// <summary>
/// The actor's own memory of enemy bases. Only local photographs feed it, so it never grants sight: a remembered
/// structure is forgotten once a later photograph covers its position without it. No structure existed in memory
/// before this, so nothing proactive could choose a nest to clear.
/// </summary>
public sealed record NestMemory(int Version, string WorldId, IReadOnlyList<NestSighting> Structures)
{
    public const int MaximumStructures = 4000;
    public const double LinkDistance = 24;

    /// <summary>Spawners defend this far around themselves; a remembered one keeps a body this close unvisited.</summary>
    public const double SpawnerGuardRadius = 48;
    public const double WormGuardMargin = 8;

    public static NestMemory Empty(string worldId) => new(1, worldId, []);

    /// <summary>A remembered worm reaches the position, or a remembered spawner's defenders would meet the actor there.</summary>
    public bool Guards(MapPosition position) => Structures.Any(s => s.Range is { } range
        ? s.Position.DistanceTo(position) <= range + WormGuardMargin
        : s.Position.DistanceTo(position) <= SpawnerGuardRadius);

    public NestMemory Observe(SpatialSnapshot map)
    {
        if (map.Scope.WorldId != WorldId) throw new InvalidDataException("Nest memory belongs to another world.");
        var seen = NestAssaultPlanner.Structures(map)
            .Select(s => new NestSighting(s.Id, s.Name, s.Position, s.Range, map.CollectedTick))
            .ToDictionary(s => s.Id, StringComparer.Ordinal);
        // The photograph is complete within its bounds: an absent remembered structure there was destroyed.
        var kept = Structures.Where(s => !seen.ContainsKey(s.Id) && !map.Bounds.Contains(s.Position));
        var all = kept.Concat(seen.Values).OrderByDescending(s => s.Tick).ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(MaximumStructures).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        return this with { Structures = all };
    }

    public IReadOnlyList<NestCluster> Clusters()
    {
        var remaining = Structures.ToList();
        var clusters = new List<NestCluster>();
        while (remaining.Count > 0)
        {
            var group = new List<NestSighting> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < group.Count; i++)
                for (int j = remaining.Count - 1; j >= 0; j--)
                    if (remaining[j].Position.DistanceTo(group[i].Position) <= LinkDistance)
                    {
                        group.Add(remaining[j]);
                        remaining.RemoveAt(j);
                    }
            var center = new MapPosition(group.Average(s => s.Position.X), group.Average(s => s.Position.Y));
            clusters.Add(new(center, group.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray()));
        }
        return clusters;
    }
}
