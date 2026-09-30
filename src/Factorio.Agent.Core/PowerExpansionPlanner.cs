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

    /// <summary>Chains of the boilers that receive water; a boiler without it makes no steam, so its engines would never run.</summary>
    public static IReadOnlyList<SteamChain> Chains(SpatialSnapshot map, string force)
    {
        var byId = map.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var watered = Watered(map, force);
        var chains = new List<SteamChain>();
        foreach (var boiler in map.Entities.Where(e => watered.Contains(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal))
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

    /// <summary>
    /// Boilers whose water comes from an observed offshore pump, directly, through pipes or through other boilers' water ports.
    /// A stray boiler from an aborted build, or one whose pump was destroyed, is left out.
    /// </summary>
    public static IReadOnlySet<string> Watered(SpatialSnapshot map, string force)
    {
        var own = map.Entities.Where(e => e.Force == force).ToDictionary(e => e.Id, StringComparer.Ordinal);
        string Type(SpatialEntity entity) => map.Prototypes[entity.Name].Type;
        var seen = own.Values.Where(e => Type(e) == "offshore-pump").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<string>(seen);
        while (queue.TryDequeue(out var id))
        {
            var entity = own[id];
            foreach (var link in entity.FluidConnections ?? [])
            {
                // Water continues through a boiler's input box only; its output box carries steam to the engines.
                if (Type(entity) == "boiler" && map.Prototypes[entity.Name].FluidBoxes?.FirstOrDefault(b => b.Index == link.BoxIndex)?.ProductionType == "output")
                    continue;
                if (link.TargetEntityId is { } target && own.TryGetValue(target, out var next) && Type(next) != "generator" && seen.Add(target))
                    queue.Enqueue(target);
            }
        }
        return own.Values.Where(e => Type(e) == "boiler" && seen.Contains(e.Id)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Completes the first under-equipped boiler, otherwise adds one boiler with its full set of engines.</summary>
    public SteamExpansionPlan? Next(SpatialSnapshot map, string boilerItem, string engineItem, string force)
    {
        int ratio = EnginesPerBoiler(Geometry(map, boilerItem), Geometry(map, engineItem));
        var chains = Chains(map, force);
        foreach (var chain in chains.Where(c => c.Engines.Count < ratio))
            if (Complete(map, chain, engineItem, ratio) is { Count: > 0 } added)
                return new("complete", chain.Boiler.Id, added.Select(a => a.Machine).ToArray(), added.Select(a => a.Link).ToArray());
        return chains.Select(c => Unit(map, c, boilerItem, engineItem, ratio)).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// The room the installation needs to keep growing, as planned entities: the missing engines of every watered chain,
    /// then up to <paramref name="units"/> units each planned from the previous one, exactly as later steps would build them.
    /// A band or pole placed just past the next unit would block every later one, so several units are reserved.
    /// </summary>
    public IReadOnlyList<SpatialEntity> Growth(SpatialSnapshot map, string boilerItem, string engineItem, string force, int units)
    {
        if (units is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(units));
        int ratio = EnginesPerBoiler(Geometry(map, boilerItem), Geometry(map, engineItem));
        var planned = map;
        foreach (var chain in Chains(map, force))
        {
            foreach (var (machine, _) in Complete(planned, chain, engineItem, ratio))
                planned = SteamPowerPlanner.Add(planned, $"growth:{chain.Boiler.Id}:{machine.Role}", Geometry(planned, machine.Item), machine.Placement);
            var tail = chain;
            for (int unit = 1; unit <= units && Unit(planned, tail, boilerItem, engineItem, ratio) is { } next; unit++)
            {
                string prefix = $"growth:{chain.Boiler.Id}:{unit}";
                foreach (var machine in next.Machines)
                    planned = SteamPowerPlanner.Add(planned, $"{prefix}:{machine.Role}", Geometry(planned, machine.Item), machine.Placement);
                tail = new(planned.Entities.Single(e => e.Id == $"planned:{prefix}:boiler"), []);
            }
        }
        return planned.Entities.Skip(map.Entities.Count).ToArray();
    }

    /// <summary>Marks the installation's growth as occupied, so feeders, poles and other auxiliaries never take its space.</summary>
    public SpatialSnapshot ReserveGrowth(SpatialSnapshot map, string boilerItem, string engineItem, string force, int units = 1) =>
        map with { Entities = [.. map.Entities, .. Growth(map, boilerItem, engineItem, force, units)] };

    /// <summary>Engines an under-equipped chain still needs; only the chain ends have a free steam port, tail engine first.</summary>
    private static List<(PlannedMachine Machine, SteamLink Link)> Complete(SpatialSnapshot map, SteamChain chain, string engineItem, int ratio)
    {
        foreach (var source in chain.Engines.Reverse().Append(chain.Boiler))
        {
            var added = Extend(map, map.Prototypes[source.Name], new(source.Position, source.Direction, 0), source.Id, engineItem,
                Math.Max(0, ratio - chain.Engines.Count), 1);
            if (added.Count > 0) return added;
        }
        return [];
    }

    private static SteamExpansionPlan? Unit(SpatialSnapshot map, SteamChain chain, string boilerItem, string engineItem, int ratio)
    {
        EntityGeometry source = map.Prototypes[chain.Boiler.Name], boiler = Geometry(map, boilerItem);
        // Facing away from the source boiler puts the new engines on the other side, so consecutive units alternate sides and
        // every engine row keeps a free neighbour row for its poles and feeder. The choice never depends on where the actor stands.
        foreach (var water in new FluidConnectionPlanner().Find(new(map), source, new(chain.Boiler.Position, chain.Boiler.Direction, 0), boilerItem, "water")
            .OrderBy(w => w.Placement.Direction == chain.Boiler.Direction ? 1 : 0).ThenBy(w => w.Placement.Position.Y).ThenBy(w => w.Placement.Position.X)
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
