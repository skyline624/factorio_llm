namespace Factorio.Agent.Core;

public sealed class MachineOutputBufferPlanner
{
    public PlacementCandidate? Find(SpatialSnapshot map, string machineId, string containerItem, BeltTransportEquipment equipment,
        CancellationToken cancellationToken = default)
    {
        var machine = map.Entities.Single(e => e.Id == machineId);
        var container = map.Prototypes[map.Items[containerItem].EntityName];
        if (container.Type != "container") throw new InvalidDataException("Output storage requires native container geometry.");
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var candidate in new PlacementPlanner().FindCandidates(field, containerItem, machine.Position, requireBuildReach: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = map with { Entities = [.. map.Entities, new("planned:output-storage", container.Name, candidate.Position,
                container.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position), candidate.Direction, machine.Force)] };
            if (new BeltTransportPlanner().Find(projected, equipment, machineId, "planned:output-storage", cancellationToken) is not null)
                return candidate;
        }
        return null;
    }
}
