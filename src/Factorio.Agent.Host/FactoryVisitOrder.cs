using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Orders known factory stops geometrically; native navigation still validates every actual approach.</summary>
internal static class FactoryVisitOrder
{
    public static IReadOnlyList<string> Plan(FactorySnapshot snapshot, IEnumerable<string> destinations)
    {
        var original = destinations.Distinct(StringComparer.Ordinal).ToArray();
        if (original.Length < 2) return original;
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        var start = actor.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        var requested = original.ToHashSet(StringComparer.Ordinal);
        var positions = snapshot.Records.Where(r => r.Kind == "entity" && requested.Contains(r.EntityId)
                && r.Data.GetProperty("role").GetString() == "factory")
            .ToDictionary(r => r.EntityId, r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, StringComparer.Ordinal);
        var known = original.Where(positions.ContainsKey).ToArray();
        var pending = new HashSet<string>(known, StringComparer.Ordinal);
        var nearest = new List<string>();
        var at = start;
        while (pending.Count > 0)
        {
            string next = pending.OrderBy(id => at.DistanceTo(positions[id])).ThenBy(id => id, StringComparer.Ordinal).First();
            nearest.Add(next);
            pending.Remove(next);
            at = positions[next];
        }
        // Nearest-neighbour is a heuristic: keep the original order if it would lengthen this open tour.
        var ordered = Distance(start, nearest, positions) < Distance(start, known, positions) ? nearest.ToArray() : known;
        return [.. ordered, .. original.Where(id => !positions.ContainsKey(id))];
    }

    internal static double Distance(MapPosition start, IEnumerable<string> route, IReadOnlyDictionary<string, MapPosition> positions)
    {
        double distance = 0;
        var at = start;
        foreach (string id in route) { distance += at.DistanceTo(positions[id]); at = positions[id]; }
        return distance;
    }
}
