namespace Factorio.Agent.Core;

public sealed record BeltTransportInstallation(string SourceInserterId, string TargetInserterId, IReadOnlyList<string> BeltIds);

public static class BeltTransportNetwork
{
    public static BeltTransportInstallation? Find(SpatialSnapshot map, string sourceId, string targetId)
        => FindSegment(map, sourceId, targetId, false);

    internal static BeltTransportInstallation? FindSegment(SpatialSnapshot map, string sourceId, string targetId, bool upstreamAccounted = true)
    {
        foreach (var arm in map.Entities.Where(e => e.PickupTargetId == sourceId && e.DropTargetId is not null))
        {
            var belts = new List<string>();
            string? current = arm.DropTargetId;
            while (current is not null && belts.Count < 200 && !belts.Contains(current))
            {
                var belt = map.Entities.SingleOrDefault(e => e.Id == current);
                if (belt?.BeltConnections is null || map.Prototypes[belt.Name].Type != "transport-belt") break;
                belts.Add(current);
                if (belt.BeltConnections.Outputs.Count == 0)
                {
                    var targetArm = map.Entities.SingleOrDefault(e => e.PickupTargetId == current && e.DropTargetId == targetId);
                    if (targetArm is null) break;
                    VerifySegment(map, sourceId, targetId, arm.Id, targetArm.Id, belts, upstreamAccounted);
                    return new(arm.Id, targetArm.Id, belts);
                }
                if (belt.BeltConnections.Outputs.Count != 1) break;
                current = belt.BeltConnections.Outputs[0];
            }
        }
        return null;
    }

    public static void Verify(SpatialSnapshot map, string sourceId, string targetId, string sourceArmId, string targetArmId,
        IReadOnlyList<string> beltIds)
        => VerifySegment(map, sourceId, targetId, sourceArmId, targetArmId, beltIds, false);

    internal static void VerifySegment(SpatialSnapshot map, string sourceId, string targetId, string sourceArmId, string targetArmId,
        IReadOnlyList<string> beltIds, bool upstreamAccounted = true)
    {
        if (beltIds.Count == 0 || beltIds.Distinct(StringComparer.Ordinal).Count() != beltIds.Count)
            throw new InvalidDataException("A transport line needs unique native belt identities.");
        var sourceArm = map.Entities.Single(e => e.Id == sourceArmId);
        var targetArm = map.Entities.Single(e => e.Id == targetArmId);
        var source = map.Entities.Single(e => e.Id == sourceId);
        if (!upstreamAccounted && map.Prototypes[source.Name].Type == "container" && map.Entities.Any(e => e.DropTargetId == sourceId))
            throw new InvalidDataException("A replenished source container requires upstream production accounting.");
        if (map.Entities.Single(e => e.Id == targetId).Force != source.Force || sourceArm.Force != source.Force || targetArm.Force != source.Force)
            throw new InvalidDataException("Transport endpoints and inserters must belong to the same force.");
        if (sourceArm.PickupTargetId != sourceId || sourceArm.DropTargetId != beltIds[0]
            || targetArm.PickupTargetId != beltIds[^1] || targetArm.DropTargetId != targetId
            || sourceArm.Power?.NetworkId is null || targetArm.Power?.NetworkId is null)
            throw new InvalidDataException("The native transport inserters do not connect the planned endpoints and power networks.");
        for (int i = 0; i < beltIds.Count; i++)
        {
            var belt = map.Entities.Single(e => e.Id == beltIds[i]);
            var connections = belt.BeltConnections ?? throw new InvalidDataException("Missing native belt connections.");
            string[] input = i == 0 ? [] : [beltIds[i - 1]];
            string[] output = i == beltIds.Count - 1 ? [] : [beltIds[i + 1]];
            if (belt.Force != source.Force || map.Prototypes[belt.Name].Type != "transport-belt" || !connections.Inputs.SequenceEqual(input)
                || !connections.Outputs.SequenceEqual(output))
                throw new InvalidDataException("The native belt graph has an unexpected connection or direction.");
        }
        var ids = beltIds.ToHashSet(StringComparer.Ordinal);
        if (map.Entities.Any(e => e.Id != sourceArmId && e.Id != targetArmId
            && ((e.PickupTargetId is not null && (ids.Contains(e.PickupTargetId) || e.PickupTargetId == sourceId))
                || (e.DropTargetId is not null && ids.Contains(e.DropTargetId)))))
            throw new InvalidDataException("Another native machine can alter the isolated transport flow.");
    }
}
