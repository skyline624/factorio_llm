namespace Factorio.Agent.Core;

/// <summary>An observed boiler and the generators its steam reaches through direct native connections, in chain order.</summary>
public sealed record SteamChain(SpatialEntity Boiler, IReadOnlyList<SpatialEntity> Engines);
/// <summary>A planned fluid link; Source and Target are observed entity ids or planned machine roles.</summary>
public sealed record SteamLink(string Source, string Target, FluidConnectionPlacement Connection);
/// <summary>"complete" adds engines to an under-equipped boiler; "unit" adds a boiler fed from an existing boiler's water port.</summary>
public sealed record SteamExpansionPlan(string Kind, string BoilerId, IReadOnlyList<PlannedMachine> Machines, IReadOnlyList<SteamLink> Links);

/// <summary>
/// Grows an observed steam installation from native fluid ports. Boilers pass water through their second water port,
/// and engines pass steam through their second steam port, so every addition is a direct chain without pipes.
/// The number of engines per boiler comes from native boiler heat and generator output.
/// </summary>
public sealed class PowerExpansionPlanner
{
    private const int CandidatesPerLink = 8;

    public static int EnginesPerBoiler(EntityGeometry boiler, EntityGeometry engine)
    {
        if (boiler.EnergyPerTick is not > 0 || engine.MaxPowerOutput is not > 0)
            throw new InvalidDataException("Missing native boiler heat or generator output.");
        return Math.Max(1, (int)Math.Floor(boiler.EnergyPerTick.Value * (boiler.BurnerEffectivity ?? 1) / engine.MaxPowerOutput.Value + 1e-9));
    }

    public static IReadOnlyList<SteamChain> Chains(SpatialSnapshot map, string force)
    {
        var byId = map.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var chains = new List<SteamChain>();
        foreach (var boiler in map.Entities.Where(e => e.Force == force && map.Prototypes[e.Name].Type == "boiler").OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var engines = new List<SpatialEntity>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { boiler.Id };
            var queue = new Queue<SpatialEntity>([boiler]);
            while (queue.TryDequeue(out var current))
                foreach (var link in current.FluidConnections ?? [])
                    if (link.TargetEntityId is { } id && byId.TryGetValue(id, out var target) && target.Force == force
                        && map.Prototypes[target.Name].Type == "generator" && seen.Add(id))
                    {
                        engines.Add(target);
                        queue.Enqueue(target);
                    }
            chains.Add(new(boiler, engines));
        }
        return chains;
    }

    /// <summary>Completes the first under-equipped boiler, otherwise adds one boiler with its full set of engines.</summary>
    public SteamExpansionPlan? Next(SpatialSnapshot map, string boilerItem, string engineItem, string force)
    {
        int ratio = EnginesPerBoiler(Geometry(map, boilerItem), Geometry(map, engineItem));
        var chains = Chains(map, force);
        foreach (var chain in chains.Where(c => c.Engines.Count < ratio))
            // Only the chain ends have a free steam port; try the tail engine first, then the boiler itself.
            foreach (var source in chain.Engines.Reverse().Append(chain.Boiler))
            {
                var added = Extend(map, map.Prototypes[source.Name], new(source.Position, source.Direction, 0), source.Id, engineItem,
                    ratio - chain.Engines.Count, 1);
                if (added.Count > 0) return new("complete", chain.Boiler.Id, added.Select(a => a.Machine).ToArray(), added.Select(a => a.Link).ToArray());
            }
        return chains.Select(c => Unit(map, c, boilerItem, engineItem, ratio)).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// Marks the next unit of every chain as occupied, so feeders, poles and other auxiliaries never take the space
    /// the installation needs to keep growing.
    /// </summary>
    public SpatialSnapshot ReserveGrowth(SpatialSnapshot map, string boilerItem, string engineItem, string force)
    {
        int ratio = EnginesPerBoiler(Geometry(map, boilerItem), Geometry(map, engineItem));
        foreach (var chain in Chains(map, force))
            if (Unit(map, chain, boilerItem, engineItem, ratio) is { } unit)
                foreach (var machine in unit.Machines)
                    map = SteamPowerPlanner.Add(map, $"growth:{chain.Boiler.Id}:{machine.Role}", Geometry(map, machine.Item), machine.Placement);
        return map;
    }

    private static SteamExpansionPlan? Unit(SpatialSnapshot map, SteamChain chain, string boilerItem, string engineItem, int ratio)
    {
        EntityGeometry source = map.Prototypes[chain.Boiler.Name], boiler = Geometry(map, boilerItem);
        foreach (var water in new FluidConnectionPlanner().Find(new(map), source, new(chain.Boiler.Position, chain.Boiler.Direction, 0), boilerItem, "water")
            .Take(CandidatesPerLink))
        {
            var engines = Extend(SteamPowerPlanner.Add(map, "boiler", boiler, water.Placement), boiler, water.Placement, "boiler", engineItem, ratio, 1);
            if (engines.Count == ratio)
                return new("unit", chain.Boiler.Id, [new("boiler", boilerItem, water.Placement), .. engines.Select(e => e.Machine)],
                    [new(chain.Boiler.Id, "boiler", water), .. engines.Select(e => e.Link)]);
        }
        return null;
    }

    /// <summary>The longest chain of up to <paramref name="count"/> engines that can follow a steam source.</summary>
    private static List<(PlannedMachine Machine, SteamLink Link)> Extend(SpatialSnapshot map, EntityGeometry sourceGeometry, PlacementCandidate source,
        string sourceRef, string engineItem, int count, int index)
    {
        var best = new List<(PlannedMachine, SteamLink)>();
        if (count == 0) return best;
        EntityGeometry engine = Geometry(map, engineItem);
        foreach (var link in new FluidConnectionPlanner().Find(new(map), sourceGeometry, source, engineItem, "steam").Take(CandidatesPerLink))
        {
            string role = $"engine-{index}";
            var rest = Extend(SteamPowerPlanner.Add(map, role, engine, link.Placement), engine, link.Placement, role, engineItem, count - 1, index + 1);
            if (rest.Count + 1 > best.Count) best = [(new(role, engineItem, link.Placement), new(sourceRef, role, link)), .. rest];
            if (best.Count == count) break;
        }
        return best;
    }

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));
}
