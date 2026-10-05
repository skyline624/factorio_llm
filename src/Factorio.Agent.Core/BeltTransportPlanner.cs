namespace Factorio.Agent.Core;

public sealed record BeltTransportEquipment(string Belt, string Inserter, string Pole);
public sealed record BeltTransportPlan(PlacementCandidate SourceInserter, PlacementCandidate TargetInserter,
    IReadOnlyList<PlacementCandidate> Belts, IReadOnlyList<PlacementCandidate> Poles);

public sealed class BeltTransportPlanner
{
    public BeltTransportPlan? Find(SpatialSnapshot map, BeltTransportEquipment equipment, string sourceId, string targetId,
        CancellationToken cancellationToken = default, int maximumBelts = 200, int nodeBudget = 12000)
    {
        if (maximumBelts is < 1 or > 1536 || nodeBudget is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumBelts));
        var source = map.Entities.Single(e => e.Id == sourceId);
        var target = map.Entities.Single(e => e.Id == targetId);
        var arm = map.Prototypes[map.Items[equipment.Inserter].EntityName];
        var pole = map.Prototypes[map.Items[equipment.Pole].EntityName];
        var belt = map.Prototypes[map.Items[equipment.Belt].EntityName];
        if (sourceId == targetId || source.Force != target.Force || arm.Type != "inserter" || !arm.IsElectric
            || arm.InserterPickup is null || arm.InserterDrop is null || pole.Type != "electric-pole"
            || pole.SupplyArea is not > 0 || pole.MaxWireDistance is not > 0)
            throw new InvalidDataException("Transport requires distinct own endpoints and native electric inserter/pole geometry.");
        var clearMap = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
        var field = new SpatialCollisionField(clearMap);
        var placements = new PlacementPlanner();
        var outputs = placements.FindCandidates(field, equipment.Inserter, source.Position, requireBuildReach: false,
            eligible: p => source.Bounds.Contains(At(p, arm.InserterPickup)), cancellationToken: cancellationToken);
        var inputs = placements.FindCandidates(field, equipment.Inserter, target.Position, requireBuildReach: false,
            eligible: p => target.Bounds.Contains(At(p, arm.InserterDrop)), cancellationToken: cancellationToken);
        var pairs = (from output in outputs from input in inputs select (Output: output, Input: input))
            .OrderBy(p => At(p.Output, arm.InserterDrop).DistanceTo(At(p.Input, arm.InserterPickup)));
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = BeltRoutePlanner.Cell(At(pair.Output, arm.InserterDrop));
            var finish = BeltRoutePlanner.Cell(At(pair.Input, arm.InserterPickup));
            if (Math.Abs(start.X - finish.X) + Math.Abs(start.Y - finish.Y) + 1 > maximumBelts) continue;
            var projected = clearMap with { Entities = [.. clearMap.Entities, ProjectArm("planned:source-arm", pair.Output)] };
            if (!new SpatialCollisionField(projected).PlacementClear(arm, pair.Input.Position, pair.Input.Direction)) continue;
            projected = projected with { Entities = [.. projected.Entities, ProjectArm("planned:target-arm", pair.Input)] };
            // Power must leave the native drop/pickup belt tiles free; otherwise its nearest pole blocks our own route.
            WorldBox[] beltPorts = [belt.CollisionBox.Translate(BeltRoutePlanner.Cell(At(pair.Output, arm.InserterDrop))),
                belt.CollisionBox.Translate(BeltRoutePlanner.Cell(At(pair.Input, arm.InserterPickup)))];
            var poles = new List<PlacementCandidate>();
            bool powered = true;
            foreach (var candidate in new[] { pair.Output, pair.Input })
            {
                var box = arm.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position);
                if (projected.Entities.Any(e => Covers(e, box))) continue;
                var extension = placements.FindCandidates(new(projected), equipment.Pole, candidate.Position, requireBuildReach: false,
                    eligible: p => Coverage(p.Position, pole.SupplyArea.Value).Overlaps(box)
                        && beltPorts.All(port => !port.Overlaps(pole.CollisionBox.Rotate(p.Direction).Translate(p.Position)))
                        && projected.Entities.Any(e => e.Force == source.Force && e.Power?.NetworkId is not null
                            && map.Prototypes[e.Name].MaxWireDistance is > 0
                            && e.Position.DistanceTo(p.Position) <= Math.Min(pole.MaxWireDistance.Value, map.Prototypes[e.Name].MaxWireDistance!.Value)),
                    cancellationToken: cancellationToken).FirstOrDefault();
                if (extension is null) { powered = false; break; }
                var connection = projected.Entities.First(e => e.Force == source.Force && e.Power?.NetworkId is not null
                    && map.Prototypes[e.Name].MaxWireDistance is > 0
                    && e.Position.DistanceTo(extension.Position) <= Math.Min(pole.MaxWireDistance.Value, map.Prototypes[e.Name].MaxWireDistance!.Value));
                poles.Add(extension);
                projected = projected with { Entities = [.. projected.Entities, new($"planned:transport-pole:{poles.Count}", pole.Name,
                    extension.Position, pole.CollisionBox.Translate(extension.Position), extension.Direction, source.Force, Power: connection.Power)] };
            }
            if (!powered) continue;
            var route = new BeltRoutePlanner().Find(projected, equipment.Belt, start, finish,
                nodeBudget: nodeBudget, cancellationToken: cancellationToken);
            if (route.Status == BeltRouteStatus.BudgetExceeded) throw new TimeoutException("Belt route search exhausted its node budget.");
            if (route.Status == BeltRouteStatus.Found && route.Belts.Count <= maximumBelts)
                return new(pair.Output, pair.Input, route.Belts, poles);
        }
        return null;

        bool Covers(SpatialEntity e, WorldBox box) => e.Force == source.Force && e.Power?.NetworkId is not null
            && map.Prototypes[e.Name].SupplyArea is { } range && Coverage(e.Position, range).Overlaps(box);
        SpatialEntity ProjectArm(string id, PlacementCandidate p) => new(id, arm.Name, p.Position,
            arm.CollisionBox.Rotate(p.Direction).Translate(p.Position), p.Direction, source.Force);
    }

    private static WorldBox Coverage(MapPosition p, double range) => new(new(p.X - range, p.Y - range), new(p.X + range, p.Y + range));
    private static MapPosition At(PlacementCandidate p, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, p.Direction);
        return new(p.Position.X + rotated.X, p.Position.Y + rotated.Y);
    }
}
