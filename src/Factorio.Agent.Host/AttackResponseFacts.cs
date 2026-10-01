using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// The planner's bounded view of defense: the newest attacks per cluster with what answered them, each cluster's coverage by
/// loaded turrets and registered nests, and the actor's armor and weapon. Attacked points and entity ids stay in the private
/// log: no coordinate reaches the model.
/// </summary>
internal static class AttackResponseFacts
{
    public const int MaximumAttacks = 5, MaximumClusters = 12;
    /// <summary>Tiles beyond a cluster within which a registered nest belongs to its ring.</summary>
    private const double NestReach = 12;

    public static async Task<object> ReadAsync(string? directory, FactoryState? state, FactorySnapshot factory, DefenseFactoryState? defenses,
        JsonElement agent, CancellationToken token)
    {
        IReadOnlyList<AttackRecord> records = [];
        if (directory is not null)
            try { records = (await new AttackLog(directory).LoadAsync(factory.Scope.WorldId, token)).Records; }
            catch (Exception error) when (error is InvalidDataException or JsonException) { }
        return Build(records, state, factory, defenses, agent);
    }

    public static object Build(IReadOnlyList<AttackRecord> records, FactoryState? state, FactorySnapshot factory, DefenseFactoryState? defenses, JsonElement agent)
    {
        var clusters = state is null ? [] : IndustryClusters.Read(state, factory);
        var positions = factory.Records.Where(r => r.Kind == "entity")
            .ToDictionary(r => r.EntityId, r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, StringComparer.Ordinal);
        var nests = state?.Cells.Where(c => c.Kind == "turret" && c.Status == "ready" && c.Plan?.ContainsKey("turret") == true)
            .Select(c => c.Plan!["turret"].Position).ToArray() ?? [];
        var turrets = defenses?.Turrets ?? [];
        JsonElement loadout = agent.TryGetProperty("loadout", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        JsonElement weapon = agent.TryGetProperty("weapon", out var selected) && selected.ValueKind == JsonValueKind.Object ? selected : default;
        return new
        {
            recentAttacks = records.OrderByDescending(r => r.Tick).Take(MaximumAttacks).Select(r => new
            {
                r.Tick, r.Cluster, r.Destroyed, r.Damaged, r.Enemies, r.Reflexes, r.Direction, r.Responded, r.NestsAdded, r.Outcome
            }).ToArray(),
            clusters = clusters.OrderBy(c => c.Id, StringComparer.Ordinal).Take(MaximumClusters).Select(c =>
            {
                var members = c.Members.Where(positions.ContainsKey).Select(id => positions[id]).ToArray();
                return new
                {
                    cluster = c.Id, industry = members.Length,
                    covered = members.Count(p => DefenseDeploymentPlanner.Coverage(new("member", p), turrets) > 0),
                    nests = nests.Count(p => IndustryClusterPlanner.Distance(c.Box, p) <= NestReach)
                };
            }).ToArray(),
            clustersTruncated = clusters.Count > MaximumClusters,
            actor = new
            {
                armor = Text(loadout, "armor"), weapon = Text(weapon, "name"), ammunition = Text(weapon, "ammunition"),
                loadedRounds = weapon.ValueKind == JsonValueKind.Object && weapon.TryGetProperty("rounds", out var rounds) ? rounds.GetInt32() : 0,
                carriedMagazines = loadout.ValueKind == JsonValueKind.Object && loadout.TryGetProperty("carried", out var carried) && carried.ValueKind == JsonValueKind.Array
                    ? carried.EnumerateArray().Where(c => c.GetProperty("kind").GetString() == "ammo").Sum(c => c.GetProperty("count").GetInt32()) : 0
            },
            interpretation = "Attacks come from destroyed registered entities, damaged own entities, visible enemy units near industry and defense reflex fights. Between goals C# answers each attacked cluster with at most 4 turret nests made from stock, walls only when stocked, and equips the best obtainable armor, bullet gun and ammunition. Coverage counts industry within range of active loaded turrets; nests are registered turret cells beside the cluster."
        };
    }

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
