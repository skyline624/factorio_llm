namespace Factorio.Agent.Core;

public interface ICliffMemoryReader
{
    Task<CliffMemory> ReadCliffMemoryAsync(ActorScope scope, CancellationToken token = default);
}

/// <summary>
/// Cliff segments the actor has seen in one world, by native identity. A long trip's coarse detour knew only the cliffs of its own
/// photographs: on 2026-10-08 (seed 987654321) each new trip from a forest valley walled by cliff lines started without them, aimed
/// straight at the wall and paced the valley again for hours. A remembered cliff absent from a photograph covering it is forgotten.
/// </summary>
public sealed record CliffMemory(int Version, string WorldId, IReadOnlyDictionary<string, MapPosition> Cliffs)
{
    public static CliffMemory Empty(string worldId) => new(1, worldId, new Dictionary<string, MapPosition>());

    public CliffMemory Observe(SpatialSnapshot map)
    {
        if (map.Scope.WorldId != WorldId) throw new InvalidDataException("Cliff photograph belongs to another world.");
        var seen = map.Entities.Where(e => map.Prototypes.TryGetValue(e.Name, out var p) && p.Type == "cliff")
            .ToDictionary(e => e.Id, e => e.Position, StringComparer.Ordinal);
        var kept = Cliffs.Where(c => seen.ContainsKey(c.Key) || !map.Bounds.Contains(c.Value));
        return this with { Cliffs = kept.Concat(seen.Where(s => !Cliffs.ContainsKey(s.Key)))
            .ToDictionary(c => c.Key, c => seen.GetValueOrDefault(c.Key, c.Value), StringComparer.Ordinal) };
    }

    /// <summary>Remembered cliffs within <paramref name="margin"/> of the box spanned by two points.</summary>
    public IReadOnlyList<MapPosition> Near(MapPosition a, MapPosition b, double margin) => Cliffs.Values
        .Where(p => p.X >= Math.Min(a.X, b.X) - margin && p.X <= Math.Max(a.X, b.X) + margin
            && p.Y >= Math.Min(a.Y, b.Y) - margin && p.Y <= Math.Max(a.Y, b.Y) + margin).ToArray();
}
