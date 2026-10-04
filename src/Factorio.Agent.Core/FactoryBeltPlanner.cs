namespace Factorio.Agent.Core;

public sealed record FactoryBeltExtension(PlacementCandidate TargetInserter, IReadOnlyList<PlacementCandidate> Belts,
    IReadOnlyList<PlacementCandidate> Poles);

/// <summary>Extends the terminal belt of a single-item bus; existing consumers keep their pickup tiles.</summary>
public sealed class FactoryBeltPlanner
{
    public FactoryBeltExtension? Extend(SpatialSnapshot map, BeltTransportEquipment equipment, IReadOnlyList<string> beltIds,
        string targetId, CancellationToken token = default, IReadOnlySet<string>? retainedBelts = null)
    {
        if (beltIds.Count == 0) throw new ArgumentException("A bus extension requires its native ordered belts.");
        var tail = map.Entities.Single(e => e.Id == beltIds[^1]);
        if (tail.BeltConnections?.Outputs.Count != 0) throw new InvalidDataException("The bus tail acquired another native output.");
        var target = map.Entities.Single(e => e.Id == targetId);
        var arm = map.Prototypes[map.Items[equipment.Inserter].EntityName];
        var pole = map.Prototypes[map.Items[equipment.Pole].EntityName];
        if (arm.FilterSlots is not > 0 || arm.InserterPickup is null || arm.InserterDrop is null)
            throw new InvalidDataException("The bus needs native filter-inserter geometry.");
        var vacant = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
        var placements = new PlacementPlanner();
        foreach (var candidate in placements.FindCandidates(new(vacant), equipment.Inserter, target.Position, requireBuildReach: false)
            .Where(p => target.Bounds.Contains(At(p, arm.InserterDrop))).OrderBy(p => p.Position.DistanceTo(tail.Position)).Take(32))
        {
            token.ThrowIfCancellationRequested();
            var projected = vacant with { Entities = [.. vacant.Entities, new SpatialEntity("planned:bus-input", arm.Name, candidate.Position,
                arm.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position), candidate.Direction, target.Force)] };
            var poles = new List<PlacementCandidate>();
            if (!Covered(projected, candidate.Position))
            {
                var extension = placements.FindCandidates(new(projected), equipment.Pole, candidate.Position, requireBuildReach: false)
                    .FirstOrDefault(p => p.Position.DistanceTo(candidate.Position) <= pole.SupplyArea
                        && projected.Entities.Any(e => e.Force == target.Force && e.Power?.NetworkId is not null
                            && map.Prototypes[e.Name].MaxWireDistance is { } reach && e.Position.DistanceTo(p.Position) <= Math.Min(reach, pole.MaxWireDistance ?? 0)));
                if (extension is null) continue;
                poles.Add(extension);
                projected = projected with { Entities = [.. projected.Entities, new SpatialEntity("planned:bus-pole", pole.Name,
                    extension.Position, pole.CollisionBox.Translate(extension.Position), 0, target.Force)] };
            }
            // The old tail is retained physically and rotated only after the new consumer is configured.
            // Existing pickup/drop references to this one tile remain valid while it becomes an interior belt.
            projected = projected with
            {
                Entities = projected.Entities.Where(e => e.Id != tail.Id).Select(e => e with
                {
                    PickupPosition = e.PickupPosition is { } pickup && BeltRoutePlanner.Cell(pickup) == tail.Position ? null : e.PickupPosition,
                    DropPosition = e.DropPosition is { } drop && BeltRoutePlanner.Cell(drop) == tail.Position ? null : e.DropPosition
                }).ToArray()
            };
            var route = new BeltRoutePlanner().Find(projected, equipment.Belt, tail.Position, BeltRoutePlanner.Cell(At(candidate, arm.InserterPickup)),
                cancellationToken: token, inletBeltId: beltIds.Count > 1 ? beltIds[^2] : null,
                existingBusBelts: beltIds.Concat(retainedBelts ?? new HashSet<string>()).ToHashSet(StringComparer.Ordinal));
            if (route.Status == BeltRouteStatus.Found && beltIds.Count + route.Belts.Count - 1 <= 200)
                return new(candidate, route.Belts, poles);
            if (route.Status == BeltRouteStatus.BudgetExceeded) throw new TimeoutException("The bus extension exhausted its route budget.");
        }
        return null;

        bool Covered(SpatialSnapshot current, MapPosition at) => current.Entities.Any(e => e.Force == target.Force && e.Power?.NetworkId is not null
            && map.Prototypes[e.Name].SupplyArea is { } range && Math.Abs(e.Position.X - at.X) <= range && Math.Abs(e.Position.Y - at.Y) <= range);
    }

    private static MapPosition At(PlacementCandidate p, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, p.Direction);
        return new(p.Position.X + rotated.X, p.Position.Y + rotated.Y);
    }
}
