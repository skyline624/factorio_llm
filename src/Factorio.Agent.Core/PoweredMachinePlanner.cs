namespace Factorio.Agent.Core;

public sealed record PoweredMachineExtension(PlacementCandidate Pole, PlacementCandidate Machine);

public sealed class PoweredMachinePlanner
{
    public PoweredMachineExtension? Extend(SpatialSnapshot map, string machineItem, string poleItem, SpatialEntity source)
    {
        EntityGeometry pole = map.Prototypes[map.Items[poleItem].EntityName];
        double range = Math.Min(pole.MaxWireDistance ?? 0, map.Prototypes[source.Name].MaxWireDistance ?? 0);
        if (source.Power?.NetworkId is null || range <= 0) return null;
        foreach (var candidate in new PlacementPlanner().FindCandidates(new(map), poleItem, source.Position, requireBuildReach: false)
            .Where(p => p.Position.DistanceTo(source.Position) <= range))
        {
            var proposed = new SpatialEntity("planned:machine-pole", pole.Name, candidate.Position,
                pole.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position), candidate.Direction, source.Force, Power: source.Power);
            SpatialSnapshot extended = map with { Entities = [.. map.Entities, proposed] };
            PlacementCandidate? placement = Place(extended, machineItem, proposed);
            if (placement is not null) return new(candidate, placement);
        }
        return null;
    }

    public PlacementCandidate? Place(SpatialSnapshot map, string machineItem, SpatialEntity pole)
    {
        if (pole.Power?.NetworkId is null) return null;
        EntityGeometry poleGeometry = map.Prototypes[pole.Name];
        if (poleGeometry.SupplyArea is not > 0) return null;
        EntityGeometry machine = map.Prototypes[map.Items[machineItem].EntityName];
        if (machine.Type is not ("lab" or "assembling-machine")) throw new InvalidDataException("Expected native powered machine geometry.");
        double radius = poleGeometry.SupplyArea.Value;
        var coverage = new WorldBox(new(pole.Position.X - radius, pole.Position.Y - radius), new(pole.Position.X + radius, pole.Position.Y + radius));
        return new PlacementPlanner().FindCandidates(new(map), machineItem, pole.Position, requireBuildReach: false)
            .FirstOrDefault(p => coverage.Overlaps(machine.CollisionBox.Rotate(p.Direction).Translate(p.Position)));
    }
}
