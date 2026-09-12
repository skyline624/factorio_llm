namespace Factorio.Agent.Core;

public sealed record ResourceExtractionPlacement(string ResourceId, PlacementCandidate Machine,
    string PoleId, PlacementCandidate? AdditionalPole);

/// <summary>Places a native electric fluid extractor on a resource with a proven local power connection.</summary>
public sealed class ResourceExtractionPlanner
{
    public ResourceExtractionPlacement? Find(SpatialSnapshot map, string resourceName, string machineItem,
        string poleItem, IReadOnlySet<string> ownedPoleIds)
    {
        EntityGeometry machine = map.Prototypes[map.Items[machineItem].EntityName];
        EntityGeometry poleGeometry = map.Prototypes[map.Items[poleItem].EntityName];
        if (machine.Type != "mining-drill" || !machine.IsElectric || machine.MiningRadius is not > 0
            || machine.FluidBoxes?.Any(b => b.ProductionType == "output") != true
            || poleGeometry.Type != "electric-pole" || poleGeometry.SupplyArea is not > 0 || poleGeometry.MaxWireDistance is not > 0) return null;
        // The controlled character will leave the fixed site through BuildAtAsync before native placement validation.
        var buildMap = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
        var field = new SpatialCollisionField(buildMap);
        var sources = map.Entities.Where(e => ownedPoleIds.Contains(e.Id) && e.Power?.NetworkId is not null
            && map.Prototypes[e.Name].Type == "electric-pole").ToArray();
        foreach (var resource in map.Entities.Where(e => e.Name == resourceName && e.Amount > 0
            && map.Prototypes[e.Name].Type == "resource").OrderBy(e => e.Position.DistanceTo(map.Actor.Position)))
        {
            if (map.Prototypes[resource.Name].ResourceCategory is not { } category
                || machine.ResourceCategories?.GetValueOrDefault(category) != true) continue;
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
    private static bool Supplies(MapPosition position, EntityGeometry pole, WorldBox machine)
    {
        double radius = pole.SupplyArea ?? 0;
        return radius > 0 && new WorldBox(new(position.X - radius, position.Y - radius),
            new(position.X + radius, position.Y + radius)).Overlaps(machine);
    }
}
