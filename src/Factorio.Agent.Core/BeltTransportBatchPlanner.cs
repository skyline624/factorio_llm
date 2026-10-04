namespace Factorio.Agent.Core;

public sealed record PlannedBeltLink(string SourceId, string TargetId, BeltTransportPlan Plan);
public sealed record BeltTransportBatchPlan(IReadOnlyList<PlannedBeltLink> Links, int Searches, bool BudgetExhausted);

/// <summary>Compares bounded route orders before one ingredient bus consumes another's chest port or passage.</summary>
public sealed class BeltTransportBatchPlanner
{
    public BeltTransportBatchPlan Find(SpatialSnapshot map, BeltTransportEquipment equipment, IReadOnlyList<string> sourceIds,
        string targetId, int maximumSearches = 48, CancellationToken token = default)
    {
        if (sourceIds.Count is < 1 or > 8 || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Count)
            throw new ArgumentException("A transport batch requires one to eight distinct sources.", nameof(sourceIds));
        if (maximumSearches is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumSearches));
        var best = new List<PlannedBeltLink>();
        var selected = new List<PlannedBeltLink>();
        int searches = 0, bestBelts = int.MaxValue, bestPoles = int.MaxValue;
        bool exhausted = false;
        Search(map, sourceIds);
        return new(best.AsReadOnly(), searches, exhausted);

        void Search(SpatialSnapshot current, IReadOnlyList<string> remaining)
        {
            token.ThrowIfCancellationRequested();
            int belts = selected.Sum(l => l.Plan.Belts.Count), poles = selected.Sum(l => l.Plan.Poles.Count);
            if (selected.Count > best.Count || selected.Count == best.Count
                && (belts < bestBelts || belts == bestBelts && poles < bestPoles))
            {
                best = [.. selected];
                bestBelts = belts;
                bestPoles = poles;
            }
            foreach (string sourceId in remaining)
            {
                if (searches == maximumSearches) { exhausted = true; return; }
                int identity = ++searches;
                var plan = new BeltTransportPlanner().Find(current, equipment, sourceId, targetId, token);
                if (plan is null) continue;
                var link = new PlannedBeltLink(sourceId, targetId, plan);
                selected.Add(link);
                Search(Project(current, equipment, link, identity), remaining.Where(s => s != sourceId).ToArray());
                selected.RemoveAt(selected.Count - 1);
            }
        }
    }

    private static SpatialSnapshot Project(SpatialSnapshot map, BeltTransportEquipment equipment, PlannedBeltLink link, int identity)
    {
        string force = map.Entities.Single(e => e.Id == link.SourceId).Force;
        var additions = new List<SpatialEntity>();
        var arm = map.Prototypes[map.Items[equipment.Inserter].EntityName];
        var belt = map.Prototypes[map.Items[equipment.Belt].EntityName];
        var pole = map.Prototypes[map.Items[equipment.Pole].EntityName];
        AddArm("source", link.Plan.SourceInserter);
        AddArm("target", link.Plan.TargetInserter);
        foreach (var part in link.Plan.Belts)
            additions.Add(new($"planned:batch:{identity}:belt:{additions.Count}", belt.Name, part.Position,
                belt.CollisionBox.Rotate(part.Direction).Translate(part.Position), part.Direction, force));
        foreach (var part in link.Plan.Poles)
        {
            var connection = map.Entities.Concat(additions).First(e => e.Force == force && e.Power?.NetworkId is not null
                && map.Prototypes[e.Name].MaxWireDistance is { } reach
                && e.Position.DistanceTo(part.Position) <= Math.Min(reach, pole.MaxWireDistance!.Value));
            additions.Add(new($"planned:batch:{identity}:pole:{additions.Count}", pole.Name, part.Position,
                pole.CollisionBox.Rotate(part.Direction).Translate(part.Position), part.Direction, force, Power: connection.Power));
        }
        return map with { Entities = [.. map.Entities, .. additions] };

        void AddArm(string role, PlacementCandidate part) => additions.Add(new($"planned:batch:{identity}:{role}", arm.Name,
            part.Position, arm.CollisionBox.Rotate(part.Direction).Translate(part.Position), part.Direction, force,
            DropPosition: At(part, arm.InserterDrop!), PickupPosition: At(part, arm.InserterPickup!)));
    }

    private static MapPosition At(PlacementCandidate placement, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, placement.Direction);
        return new(placement.Position.X + rotated.X, placement.Position.Y + rotated.Y);
    }
}
