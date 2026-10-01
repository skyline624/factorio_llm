using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Known own industry of the actor's surface as clustering members: bands, registered cells and other industry.</summary>
internal static class IndustryClusters
{
    public static IReadOnlyList<IndustryCluster> Read(FactoryState state, FactorySnapshot snapshot) =>
        IndustryClusterPlanner.Group(Members(state, snapshot));

    /// <summary>
    /// Bands (with their unbuilt slots), entities of registered production cells and every other known industrial entity.
    /// Defenses are protection, not industry; link poles, belts and pipes would chain distant sites into one unobservable ring.
    /// </summary>
    public static IReadOnlyList<IndustryMember> Members(FactoryState state, FactorySnapshot snapshot)
    {
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        static int? Surface(FactoryRecord record) => record.Data.TryGetProperty("surfaceIndex", out var index) ? index.GetInt32() : null;
        int? surface = Surface(actor);
        var defense = state.Cells.Where(c => c.Kind is "turret" or "wall").SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var cellEntities = state.Cells.Where(c => c.Kind is not ("turret" or "wall"))
            .SelectMany(c => c.Entities.Where(e => !e.Key.StartsWith("link-", StringComparison.Ordinal)).Select(e => e.Value))
            .ToHashSet(StringComparer.Ordinal);
        var members = state.Zones.Select(z => new IndustryMember($"zone-{z.Id}", z.Box, Entity: false)).ToList();
        foreach (var record in snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && Surface(r) == surface && !defense.Contains(r.EntityId)))
        {
            string type = record.Data.GetProperty("type").GetString()!;
            if (!cellEntities.Contains(record.EntityId) && !DefenseFactoryState.Industry.Contains(type)) continue;
            var at = record.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            // The photograph has no footprint: a conservative square keeps every real site inside its observed ring.
            double half = type == "rocket-silo" ? 4.5 : 2.5;
            members.Add(new(record.EntityId, new(new(at.X - half, at.Y - half), new(at.X + half, at.Y + half))));
        }
        return members;
    }
}
