namespace Factorio.Agent.Core;

public sealed record ResourceExtractionPlacement(string ResourceId, PlacementCandidate Machine,
    string PoleId, PlacementCandidate? AdditionalPole);

public sealed record ResourceExtractionSite(string ResourceId, PlacementCandidate Machine, string? ExistingMachineId = null);

/// <summary>Places a native electric fluid extractor on a resource with a proven local power connection.</summary>
public sealed class ResourceExtractionPlanner
{
    public ResourceExtractionSite? FindSite(SpatialSnapshot map, string resourceName, string machineItem,
        IReadOnlySet<string> ownedIds)
    {
        EntityGeometry machine = map.Prototypes[map.Items[machineItem].EntityName];
        if (!IsFluidExtractor(machine)) return null;
        var resources = CompatibleResources(map, resourceName, machine).ToArray();
        foreach (var resource in resources)
        {
            var installed = map.Entities.Where(e => ownedIds.Contains(e.Id) && e.Name == machine.Name
                && e.Position.DistanceTo(resource.Position) <= machine.MiningRadius!.Value)
                .OrderBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
            if (installed is not null) return new(resource.Id,
                new(installed.Position, installed.Direction, installed.Position.DistanceTo(map.Actor.Position)), installed.Id);
        }
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var resource in resources)
            for (int direction = 0; direction < 16; direction += 4)
                if (field.PlacementClear(machine, resource.Position, direction))
                    return new(resource.Id, new(resource.Position, direction, resource.Position.DistanceTo(map.Actor.Position)));
        return null;
    }

    public ResourceExtractionPlacement? Find(SpatialSnapshot map, string resourceName, string machineItem,
        string poleItem, IReadOnlySet<string> ownedPoleIds)
    {
        EntityGeometry machine = map.Prototypes[map.Items[machineItem].EntityName];
        EntityGeometry poleGeometry = map.Prototypes[map.Items[poleItem].EntityName];
        if (!IsFluidExtractor(machine) || poleGeometry.Type != "electric-pole"
            || poleGeometry.SupplyArea is not > 0 || poleGeometry.MaxWireDistance is not > 0) return null;
        // The controlled character will leave the fixed site through BuildAtAsync before native placement validation.
        var buildMap = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
        var field = new SpatialCollisionField(buildMap);
        var sources = map.Entities.Where(e => ownedPoleIds.Contains(e.Id) && e.Power?.NetworkId is not null
            && map.Prototypes[e.Name].Type == "electric-pole").ToArray();
        foreach (var resource in CompatibleResources(map, resourceName, machine))
        {
            for (int direction = 0; direction < 16; direction += 4)
            {
                var position = resource.Position;
                if (!field.PlacementClear(machine, position, direction)) continue;
                var placement = new PlacementCandidate(position, direction, position.DistanceTo(map.Actor.Position));
                WorldBox footprint = machine.CollisionBox.Rotate(direction).Translate(position);
                foreach (var source in sources.OrderBy(p => p.Position.DistanceTo(position)))
                {
                    EntityGeometry sourceGeometry = map.Prototypes[source.Name];
                    if (Supplies(source.Position, sourceGeometry, footprint)) return new(resource.Id, placement, source.Id, null);
                    double wire = Math.Min(sourceGeometry.MaxWireDistance ?? 0, poleGeometry.MaxWireDistance.Value);
                    var occupied = buildMap with
                    {
                        Entities = [..buildMap.Entities, new SpatialEntity("planned:extractor", machine.Name,
                        position, footprint, direction, source.Force)]
                    };
                    foreach (var candidate in new PlacementPlanner().FindCandidates(new(occupied), poleItem, position, requireBuildReach: false))
                        if (candidate.Position.DistanceTo(source.Position) <= wire && Supplies(candidate.Position, poleGeometry, footprint))
                            return new(resource.Id, placement, source.Id, candidate);
                }
            }
        }
        return null;
    }

    private static bool IsFluidExtractor(EntityGeometry machine) => machine.Type == "mining-drill" && machine.IsElectric
        && machine.MiningRadius is > 0 && machine.FluidBoxes?.Any(b => b.ProductionType == "output") == true;

    private static IEnumerable<SpatialEntity> CompatibleResources(SpatialSnapshot map, string resourceName, EntityGeometry machine)
        => map.Entities.Where(e => e.Name == resourceName && e.Amount > 0 && map.Prototypes[e.Name].Type == "resource"
            && map.Prototypes[e.Name].ResourceCategory is { } category && machine.ResourceCategories?.GetValueOrDefault(category) == true
            && (map.StationaryThreats ?? []).All(threat => e.Position.DistanceTo(threat.Position)
                > threat.Range + SpatialCollisionField.StationaryThreatMargin))
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position));

    private static bool Supplies(MapPosition position, EntityGeometry pole, WorldBox machine)
    {
        double radius = pole.SupplyArea ?? 0;
        return radius > 0 && new WorldBox(new(position.X - radius, position.Y - radius),
            new(position.X + radius, position.Y + radius)).Overlaps(machine);
    }
}
