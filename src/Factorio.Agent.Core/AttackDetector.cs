namespace Factorio.Agent.Core;

/// <summary>A registered entity missing from the native photograph, at the position its cell plan recorded.</summary>
public sealed record DestroyedEntity(string Id, MapPosition Position);
/// <summary>A known own entity below its native maximum health; buildings never regenerate, so the damage persists.</summary>
public sealed record DamagedEntity(string Id, MapPosition Position, double Health, double MaxHealth);
/// <summary>A fight of the defense reflex: a shot at, or a retreat from, a visible enemy.</summary>
public sealed record ReflexEvent(long Tick, string Kind, MapPosition Actor, MapPosition? Enemy, int Enemies);
public sealed record AttackEvidence(IReadOnlyList<DestroyedEntity> Destroyed, IReadOnlyList<DamagedEntity> Damaged,
    IReadOnlyList<VisibleThreat> Enemies, IReadOnlyList<ReflexEvent> Reflexes);

/// <summary>
/// One detected attack on a cluster. Counts are new evidence of this detection; Direction is the compass bearing of observed
/// enemies from the cluster centre. Points and Evidence stay in the private log: coordinates never reach the planner.
/// </summary>
public sealed record AttackRecord(long Tick, string Cluster, int Destroyed, int Damaged, int Enemies, int Reflexes, string? Direction,
    IReadOnlyList<MapPosition> Points, IReadOnlyList<string> Evidence, bool Responded = false, int NestsAdded = 0, int Attempts = 0,
    string? Outcome = null);

/// <summary>What earlier detections reported, so persisting damage, a lingering enemy or an old fight is never a new attack.</summary>
public sealed record AttackMemory(IReadOnlyList<string> Destroyed, IReadOnlyDictionary<string, double> Health,
    IReadOnlyList<string> Enemies, long ReflexTick)
{
    public static AttackMemory Empty { get; } = new([], new Dictionary<string, double>(), [], -1);
}

/// <summary>
/// Turns native evidence into attack records per cluster: destroyed registered entities, newly or further damaged own
/// entities, newly seen enemy units and reflex fights near own industry. Pure and bounded; the memory deduplicates.
/// </summary>
public static class AttackDetector
{
    /// <summary>Tiles from a cluster within which enemies and fights threaten it.</summary>
    public const double NearIndustry = 24;
    public const int MaximumPoints = 16, MaximumEvidence = 8, MaximumEnemyMemory = 256;
    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    public static (IReadOnlyList<AttackRecord> Records, AttackMemory Memory) Detect(long tick, IReadOnlyList<IndustryCluster> clusters,
        AttackEvidence evidence, AttackMemory memory)
    {
        var reported = memory.Destroyed.ToHashSet(StringComparer.Ordinal);
        var seen = memory.Enemies.ToHashSet(StringComparer.Ordinal);
        var destroyed = evidence.Destroyed.Where(d => !reported.Contains(d.Id)).ToArray();
        var damaged = evidence.Damaged.Where(d => d.Health < d.MaxHealth
            && (!memory.Health.TryGetValue(d.Id, out double previous) || d.Health < previous - 1e-6)).ToArray();
        var enemies = evidence.Enemies.Where(e => !seen.Contains(e.Id)).ToArray();
        var reflexes = evidence.Reflexes.Where(r => r.Tick > memory.ReflexTick).ToArray();

        var found = new Dictionary<string, List<(string Kind, string? Id, MapPosition At, bool Enemy)>>(StringComparer.Ordinal);
        bool Add(string kind, string? id, MapPosition at, bool enemy)
        {
            var cluster = clusters.Select(c => (Cluster: c, Distance: IndustryClusterPlanner.Distance(c.Box, at)))
                .Where(p => p.Distance <= NearIndustry).OrderBy(p => p.Distance).ThenBy(p => p.Cluster.Id, StringComparer.Ordinal)
                .Select(p => p.Cluster).FirstOrDefault();
            if (cluster is null) return false;
            if (!found.TryGetValue(cluster.Id, out var list)) found[cluster.Id] = list = [];
            list.Add((kind, id, at, enemy));
            return true;
        }
        foreach (var entity in destroyed) Add("destroyed", entity.Id, entity.Position, false);
        foreach (var entity in damaged) Add("damaged", entity.Id, entity.Position, false);
        // Only enemies seen near industry are remembered: one first seen far away still counts when it closes in.
        var near = enemies.Where(e => Add("enemy", e.Id, e.Position, true)).Select(e => e.Id).ToArray();
        foreach (var fight in reflexes) Add("reflex", null, fight.Enemy ?? fight.Actor, fight.Enemy is not null);

        var records = found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            var cluster = clusters.Single(c => c.Id == p.Key);
            var hostile = p.Value.Where(e => e.Enemy).Select(e => e.At).ToArray();
            string? direction = hostile.Length == 0 ? null
                : Bearing(cluster.Center, new(hostile.Average(e => e.X), hostile.Average(e => e.Y)));
            return new AttackRecord(tick, p.Key, p.Value.Count(e => e.Kind == "destroyed"), p.Value.Count(e => e.Kind == "damaged"),
                p.Value.Count(e => e.Kind == "enemy"), p.Value.Count(e => e.Kind == "reflex"), direction,
                p.Value.Select(e => e.At).Distinct().Take(MaximumPoints).ToArray(),
                p.Value.Where(e => e.Kind is "destroyed" or "damaged").Select(e => e.Id!).Take(MaximumEvidence).ToArray());
        }).ToArray();

        // The memory follows the current world: rebuilt entities and repaired damage leave it, so a later loss is new again.
        var next = new AttackMemory(evidence.Destroyed.Select(d => d.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            evidence.Damaged.Where(d => d.Health < d.MaxHealth).GroupBy(d => d.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Min(d => d.Health), StringComparer.Ordinal),
            memory.Enemies.Concat(near).TakeLast(MaximumEnemyMemory).ToArray(),
            evidence.Reflexes.Select(r => r.Tick).Append(memory.ReflexTick).Max());
        return (records, next);
    }

    /// <summary>Eight-point bearing from one map position to another; map y grows southward.</summary>
    public static string Bearing(MapPosition from, MapPosition to)
    {
        double degrees = Math.Atan2(to.X - from.X, from.Y - to.Y) * 180 / Math.PI;
        return Compass[(int)Math.Round((degrees + 360) % 360 / 45) % 8];
    }
}
