namespace Factorio.Agent.Core;

public sealed record BalancedBeltPlan(BeltTransportPlan First, PlacementCandidate Splitter, int ReplacedBelt,
    FactoryBeltExtension Second);

/// <summary>Synthesizes two branches of a fresh single-item line; the native splitter supplies both active consumers.</summary>
public sealed class BalancedBeltPlanner
{
    public BalancedBeltPlan? Find(SpatialSnapshot map, BeltTransportEquipment equipment, string splitterItem,
        string sourceId, string firstId, string secondId, CancellationToken token = default)
    {
        if (new[] { sourceId, firstId, secondId }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("Distinct native producer and consumer endpoints are required.");
        var splitter = SplitterGeometry(map, equipment, splitterItem);
        var first = new BeltTransportPlanner().Find(map, equipment, sourceId, firstId, token);
        if (first is null) return null;
        return Branch(map, equipment, splitterItem, splitter, sourceId, secondId, first,
            new HashSet<string>(StringComparer.Ordinal), token);
    }

    /// <summary>Branches a checked native prefix; only explicitly owned obsolete transport pieces are projected away.</summary>
    public BalancedBeltPlan? FindExisting(SpatialSnapshot map, BeltTransportEquipment equipment, string splitterItem,
        string sourceId, string firstId, string secondId, string sourceArmId, string firstArmId,
        IReadOnlyList<string> prefixIds, IReadOnlyList<string> poleIds, IReadOnlySet<string> obsoleteIds,
        CancellationToken token = default)
    {
        if (new[] { sourceId, firstId, secondId }.Distinct(StringComparer.Ordinal).Count() != 3
            || prefixIds.Count is < 3 or > 200 || prefixIds.Distinct(StringComparer.Ordinal).Count() != prefixIds.Count
            || poleIds.Distinct(StringComparer.Ordinal).Count() != poleIds.Count
            || obsoleteIds.Overlaps(prefixIds.Concat(poleIds).Append(sourceArmId).Append(firstArmId)))
            throw new ArgumentException("Distinct endpoints and a bounded native prefix are required.");
        var splitter = SplitterGeometry(map, equipment, splitterItem);
        var source = map.Entities.Single(e => e.Id == sourceId);
        var sourceArm = map.Entities.Single(e => e.Id == sourceArmId);
        var firstArm = map.Entities.Single(e => e.Id == firstArmId);
        var ignored = prefixIds.Concat(poleIds).Concat(obsoleteIds).Append(sourceArmId).Append(firstArmId)
            .ToHashSet(StringComparer.Ordinal);
        if (ignored.Overlaps([sourceId, firstId, secondId]) || sourceArmId == firstArmId
            || sourceArm.Name != map.Items[equipment.Inserter].EntityName || firstArm.Name != sourceArm.Name
            || sourceArm.PickupTargetId != sourceId || sourceArm.DropTargetId != prefixIds[0]
            || firstArm.PickupTargetId != prefixIds[^1] || firstArm.DropTargetId != firstId
            || ignored.Any(id => !map.Entities.Any(e => e.Id == id && e.Force == source.Force && map.Bounds.Contains(e.Bounds)
                && map.Prototypes[e.Name].Type is "transport-belt" or "inserter" or "electric-pole")))
            throw new InvalidDataException("Existing conversion requires own observed transport and exact native endpoints.");
        var belts = prefixIds.Select(id => map.Entities.Single(e => e.Id == id)).ToArray();
        for (int i = 0; i < belts.Length; i++)
        {
            var edges = belts[i].BeltConnections;
            string[] inputs = i == 0 ? [] : [prefixIds[i - 1]];
            if (belts[i].Name != map.Items[equipment.Belt].EntityName || edges is null
                || edges.InputsCount != inputs.Length || !edges.Inputs.SequenceEqual(inputs)
                || edges.OutputsCount != edges.Outputs.Count || edges.Outputs.Count > 1
                || (i + 1 < belts.Length ? !edges.Outputs.SequenceEqual([prefixIds[i + 1]])
                    : edges.Outputs.Any(id => !obsoleteIds.Contains(id))))
                throw new InvalidDataException("Native prefix adjacency differs from the retained ordered line.");
        }
        var poles = poleIds.Select(id => map.Entities.Single(e => e.Id == id)).ToArray();
        if (poles.Any(e => e.Name != map.Items[equipment.Pole].EntityName))
            throw new InvalidDataException("Only matching native bus poles can be retained.");
        PlacementCandidate At(SpatialEntity e) => new(e.Position, e.Direction, 0);
        var first = new BeltTransportPlan(At(sourceArm), At(firstArm), belts.Select(At).ToArray(), poles.Select(At).ToArray());
        return Branch(map, equipment, splitterItem, splitter, sourceId, secondId, first, ignored, token);
    }

    private static EntityGeometry SplitterGeometry(SpatialSnapshot map, BeltTransportEquipment equipment, string splitterItem)
    {
        var splitter = map.Prototypes[map.Items[splitterItem].EntityName];
        var belt = map.Prototypes[map.Items[equipment.Belt].EntityName];
        if (splitter.Type != "splitter" || splitter.TileWidth != 2 || splitter.TileHeight != 1
            || splitter.BeltSpeed is not > 0 || splitter.BeltSpeed != belt.BeltSpeed)
            throw new InvalidDataException("A native two-wide splitter matching the ordinary belt speed is required.");
        return splitter;
    }

    private static BalancedBeltPlan? Branch(SpatialSnapshot map, BeltTransportEquipment equipment, string splitterItem,
        EntityGeometry splitter, string sourceId, string secondId, BeltTransportPlan first, IReadOnlySet<string> ignored,
        CancellationToken token)
    {
        var source = map.Entities.Single(e => e.Id == sourceId);
        SpatialEntity Placed(string id, string item, PlacementCandidate p) => new(id, map.Items[item].EntityName, p.Position,
            map.Prototypes[map.Items[item].EntityName].CollisionBox.Rotate(p.Direction).Translate(p.Position), p.Direction, source.Force,
            Power: item == equipment.Pole ? map.Entities.First(e => e.Force == source.Force && e.Power?.NetworkId is not null).Power : null);
        var oldBelts = first.Belts.Select((p, i) => Placed($"planned:first-belt:{i}", equipment.Belt, p)).ToArray();
        var occupied = map with { Entities = [.. map.Entities.Where(e => e.Id != map.Actor.Id && !ignored.Contains(e.Id)), .. oldBelts,
            Placed("planned:source-arm", equipment.Inserter, first.SourceInserter),
            Placed("planned:first-arm", equipment.Inserter, first.TargetInserter),
            .. first.Poles.Select((p, i) => Placed($"planned:first-pole:{i}", equipment.Pole, p))] };
        var retained = oldBelts.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        // Replacing an existing native piece is only a proposal here; the host must persist and verify its retirement.
        // Prefer an early split, reducing the amount of shared transport before distribution.
        for (int index = 1; index + 1 < first.Belts.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var piece = first.Belts[index];
            var forward = ExtractionPlanner.Rotate(new(0, -1), piece.Direction);
            MapPosition Offset(MapPosition p, MapPosition d) => new(p.X + d.X, p.Y + d.Y);
            if (first.Belts[index - 1].Direction != piece.Direction
                || Offset(first.Belts[index - 1].Position, forward) != piece.Position
                || Offset(piece.Position, forward) != first.Belts[index + 1].Position) continue;
            foreach (int side in new[] { 1, -1 })
            {
                var lateral = ExtractionPlanner.Rotate(new(side * .5, 0), piece.Direction);
                var candidate = new PlacementCandidate(Offset(piece.Position, lateral), piece.Direction, index);
                var vacant = occupied with { Entities = occupied.Entities.Where(e => e.Id != oldBelts[index].Id).ToArray() };
                if (!new SpatialCollisionField(vacant).PlacementClear(splitter, candidate.Position, candidate.Direction)) continue;
                var ports = Ports(splitter, candidate);
                var secondStart = ports.Outputs.Single(p => p != first.Belts[index + 1].Position);
                var splitEntity = Placed("planned:splitter", splitterItem, candidate);
                var tail = Placed("planned:branch-tail", equipment.Belt, new(secondStart, piece.Direction, 0))
                    with { BeltConnections = new([], [], 0, 0) };
                var projected = vacant with { Entities = [.. vacant.Entities, splitEntity, tail] };
                var second = new FactoryBeltPlanner().Extend(projected, equipment,
                    [splitEntity.Id, tail.Id], secondId, token, retained);
                if (second is not null && first.Belts.Count - 1 + second.Belts.Count <= 200)
                    return new(first, candidate, index, second);
            }
        }
        return null;
    }

    /// <summary>Vanilla two-wide ports derived from native dimensions and cardinal flow, never world coordinates.</summary>
    public static (MapPosition[] Inputs, MapPosition[] Outputs) Ports(EntityGeometry geometry, PlacementCandidate placement)
    {
        if (geometry.Type != "splitter" || geometry.TileWidth != 2 || geometry.TileHeight != 1)
            throw new InvalidDataException("Unsupported native splitter footprint.");
        MapPosition At(double x, double y)
        {
            var p = ExtractionPlanner.Rotate(new(x, y), placement.Direction);
            return new(placement.Position.X + p.X, placement.Position.Y + p.Y);
        }
        MapPosition[] inputs = [At(-.5, 1), At(.5, 1)];
        MapPosition[] outputs = [At(-.5, -1), At(.5, -1)];
        if (inputs.Concat(outputs).Any(p => BeltRoutePlanner.Cell(p) != p))
            throw new InvalidDataException("Splitter ports do not align with native belt tiles.");
        return (inputs, outputs);
    }
}
