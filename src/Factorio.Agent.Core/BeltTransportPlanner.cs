namespace Factorio.Agent.Core;

public sealed record BeltTransportEquipment(string Belt, string Inserter, string Pole, string? UndergroundBelt = null);
public sealed record BeltTransportPlan(PlacementCandidate SourceInserter, PlacementCandidate TargetInserter,
    IReadOnlyList<PlacementCandidate> Belts, IReadOnlyList<PlacementCandidate> Poles, string? UndergroundBeltItem = null);

public sealed class BeltTransportPlanner
{
    public BeltTransportPlan? Find(SpatialSnapshot map, BeltTransportEquipment equipment, string sourceId, string targetId,
        CancellationToken cancellationToken = default, int maximumBelts = 200, int nodeBudget = 12000,
        Func<BeltTransportPlan, bool>? eligible = null)
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
        var crossings = new List<(PlacementCandidate Output, PlacementCandidate Input, SpatialCollisionField Field,
            IReadOnlyList<PlacementCandidate> Poles, MapPosition Start, MapPosition Finish, int Remaining)>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = BeltRoutePlanner.Cell(At(pair.Output, arm.InserterDrop));
            var finish = BeltRoutePlanner.Cell(At(pair.Input, arm.InserterPickup));
            if (equipment.UndergroundBelt is null && Math.Abs(start.X - finish.X) + Math.Abs(start.Y - finish.Y) + 1 > maximumBelts) continue;
            var projectedField = field.AppendEntities([ProjectArm("planned:source-arm", pair.Output)]);
            if (!projectedField.PlacementClear(arm, pair.Input.Position, pair.Input.Direction)) continue;
            projectedField = projectedField.AppendEntities([ProjectArm("planned:target-arm", pair.Input)]);
            var projected = projectedField.Map;
            // Power must leave the native drop/pickup belt tiles free; otherwise its nearest pole blocks our own route.
            WorldBox[] beltPorts = [belt.CollisionBox.Translate(BeltRoutePlanner.Cell(At(pair.Output, arm.InserterDrop))),
                belt.CollisionBox.Translate(BeltRoutePlanner.Cell(At(pair.Input, arm.InserterPickup)))];
            var poles = new List<PlacementCandidate>();
            bool powered = true;
            foreach (var candidate in new[] { pair.Output, pair.Input })
            {
                var box = arm.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position);
                if (projected.Entities.Any(e => Covers(e, box))) continue;
                var extension = placements.FindCandidates(projectedField, equipment.Pole, candidate.Position, requireBuildReach: false,
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
                projectedField = projectedField.AppendEntities([new($"planned:transport-pole:{poles.Count}", pole.Name,
                    extension.Position, pole.CollisionBox.Translate(extension.Position), extension.Direction, source.Force, Power: connection.Power)]);
                projected = projectedField.Map;
            }
            if (!powered) continue;
            var route = new BeltRoutePlanner().FindWithField(projectedField,equipment.Belt,start,finish,nodeBudget,cancellationToken);
            if (route.Status == BeltRouteStatus.BudgetExceeded) throw new TimeoutException("Belt route search exhausted its node budget.");
            if (route.Status == BeltRouteStatus.Found && route.Belts.Count <= maximumBelts)
            {
                var plan = new BeltTransportPlan(pair.Output, pair.Input, route.Belts, poles);
                if (eligible?.Invoke(plan) != false) return plan;
            }
            if (equipment.UndergroundBelt is not null)
                crossings.Add((pair.Output, pair.Input, projectedField, poles, start, finish, nodeBudget - route.ExpandedNodes));
        }
        // Try all native arm/power layouts in surface mode first. A difficult crossing must not hide a later ordinary route.
        bool crossingBudgetExceeded = false;
        foreach (var crossing in crossings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (crossing.Remaining <= 0) { crossingBudgetExceeded = true; continue; }
            var route = new UndergroundBeltRoutePlanner().FindWithField(crossing.Field, equipment.Belt, equipment.UndergroundBelt!,
                crossing.Start, crossing.Finish, crossing.Remaining, cancellationToken);
            if (route.Status == BeltRouteStatus.BudgetExceeded) { crossingBudgetExceeded = true; continue; }
            if (route.Status == BeltRouteStatus.Found && route.Belts.Count <= maximumBelts)
            {
                var plan = new BeltTransportPlan(crossing.Output, crossing.Input, route.Belts, crossing.Poles,
                    route.Belts.Any(p => p.UndergroundType is not null) ? equipment.UndergroundBelt : null);
                if (eligible?.Invoke(plan) != false) return plan;
            }
        }
        if (crossingBudgetExceeded) throw new TimeoutException("Underground belt route search exhausted its shared node budget.");
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
