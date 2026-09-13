namespace Factorio.Agent.Core;

public sealed record PoweredMachineExtension(PlacementCandidate Pole, PlacementCandidate Machine);

public sealed class PoweredMachinePlanner
{
    public SpatialEntity? NearestSupply(SpatialSnapshot map, IReadOnlySet<string> ownPoleIds) =>
        map.Entities.Where(e => ownPoleIds.Contains(e.Id) && e.Power?.NetworkId is not null)
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();

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
        if (machine.Type is not ("lab" or "assembling-machine" or "rocket-silo")) throw new InvalidDataException("Expected native powered machine geometry.");
        double radius = poleGeometry.SupplyArea.Value;
        var coverage = new WorldBox(new(pole.Position.X - radius, pole.Position.Y - radius), new(pole.Position.X + radius, pole.Position.Y + radius));
        var field = new SpatialCollisionField(map);
        return new PlacementPlanner().FindCandidates(field, machineItem, pole.Position, requireBuildReach: false)
            .FirstOrDefault(p => coverage.Overlaps(machine.CollisionBox.Rotate(p.Direction).Translate(p.Position))
                && FluidPortsClear(field, machine, p));
    }

    private static bool FluidPortsClear(SpatialCollisionField field, EntityGeometry machine, PlacementCandidate candidate)
    {
        var map = field.Map;
        if (machine.FluidBoxes is null || machine.FluidBoxes.Count == 0) return true;
        var pipe = map.Items.Values.Select(i => map.Prototypes[i.EntityName]).FirstOrDefault(p => p.Type == "pipe");
        if (pipe is null) throw new InvalidDataException("Chemical placement requires native pipe clearance geometry.");
        foreach (var port in machine.FluidBoxes.SelectMany(b => b.Connections).Where(p => p.Type == "normal"))
        {
            if (port.Positions.Count != 4) throw new InvalidDataException("Missing cardinal fluid port geometry.");
            var offset = port.Positions[candidate.Direction / 4];
            var forward = ExtractionPlanner.Rotate(new(0, -1), (port.Direction + candidate.Direction) % 16);
            // Keep a short corridor for a pipe, its continuation and a turn without touching neighbouring networks.
            for (int distance = 1; distance <= 3; distance++)
            {
                var cell = new MapPosition(candidate.Position.X + offset.X + forward.X * distance, candidate.Position.Y + offset.Y + forward.Y * distance);
                if (!field.PlacementClear(pipe, cell, 0) || map.Entities.Any(e => e.FluidConnections?.Any(p => p.TargetPosition.DistanceTo(cell) < .01) == true))
                    return false;
            }
        }
        return true;
    }
}
