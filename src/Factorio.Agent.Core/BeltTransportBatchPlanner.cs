namespace Factorio.Agent.Core;

public sealed record PlannedBeltLink(string SourceId, string TargetId, BeltTransportPlan Plan);
public sealed record BeltTransportBatchPlan(IReadOnlyList<PlannedBeltLink> Links, int Searches, bool BudgetExhausted);
public sealed record BeltTransportRequest(string TargetId, IReadOnlyList<string> SourceIds);
public sealed record BeltTransportBatchProgress(int Search, int Selected, string SourceId, string TargetId, string Outcome, int? PhysicalBelts = null);

/// <summary>Compares bounded route orders before one ingredient bus consumes another's chest port or passage.</summary>
public sealed class BeltTransportBatchPlanner(Action<BeltTransportBatchProgress>? progress = null)
{
    public BeltTransportBatchPlan Find(SpatialSnapshot map, BeltTransportEquipment equipment, IReadOnlyList<string> sourceIds,
        string targetId, int maximumSearches = 48, CancellationToken token = default)
    {
        if (sourceIds.Count is < 1 or > 8 || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Count)
            throw new ArgumentException("A transport batch requires one to eight distinct sources.", nameof(sourceIds));
        return Find(map, equipment, sourceIds.Select(source => new BeltTransportRequest(targetId, [source])).ToArray(), maximumSearches, token);
    }

    /// <summary>Plans assignments and route order for several consumers without promising one source twice.</summary>
    public BeltTransportBatchPlan Find(SpatialSnapshot map, BeltTransportEquipment equipment, IReadOnlyList<BeltTransportRequest> requests,
        int maximumSearches = 48, CancellationToken token = default, bool stopAfterComplete = false,
        int maximumBelts = 200, int nodeBudget = 12000)
    {
        if (requests.Count is < 1 or > 8 || requests.Any(r => string.IsNullOrWhiteSpace(r.TargetId)
            || r.SourceIds.Count is < 1 or > 8 || r.SourceIds.Any(s => string.IsNullOrWhiteSpace(s) || s == r.TargetId)
            || r.SourceIds.Distinct(StringComparer.Ordinal).Count() != r.SourceIds.Count))
            throw new ArgumentException("A batch requires one to eight valid requests with distinct candidate sources.", nameof(requests));
        if (maximumSearches is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumSearches));
        if (maximumBelts is < 1 or > 1536 || nodeBudget is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumBelts));
        var best = new List<PlannedBeltLink>();
        var selected = new List<PlannedBeltLink>();
        int searches = 0, bestBelts = int.MaxValue, bestPoles = int.MaxValue;
        bool exhausted = false, complete = false;
        var used = new HashSet<string>(StringComparer.Ordinal);
        Search(map, Enumerable.Range(0, requests.Count).ToArray());
        return new(best.AsReadOnly(), searches, exhausted);

        void Search(SpatialSnapshot current, IReadOnlyList<int> remaining)
        {
            token.ThrowIfCancellationRequested();
            int belts = selected.Sum(l => l.Plan.Belts.Count), poles = selected.Sum(l => l.Plan.Poles.Count);
            if (selected.Count > best.Count || selected.Count == best.Count
                && (belts < bestBelts || belts == bestBelts && poles < bestPoles))
            {
                best = [.. selected];
                bestBelts = belts;
                bestPoles = poles;
                if (stopAfterComplete && best.Count == requests.Count) complete = true;
            }
            var field = new SpatialCollisionField(current with { Entities = current.Entities.Where(e => e.Id != current.Actor.Id).ToArray() });
            var arm = current.Prototypes[current.Items[equipment.Inserter].EntityName];
            var belt = current.Prototypes[current.Items[equipment.Belt].EntityName];
            var tunnel = equipment.UndergroundBelt is null ? null : current.Prototypes[current.Items[equipment.UndergroundBelt].EntityName];
            var routing = new BeltRoutingField(current,belt,current.Actor.Position,token,collisionField:field);
            (int Ports,int Region) Access(int index)
            {
                var target = current.Entities.Single(e => e.Id == requests[index].TargetId);
                var candidates = new PlacementPlanner().FindCandidates(field,equipment.Inserter,target.Position,requireBuildReach:false,
                    eligible:p => target.Bounds.Contains(At(p,arm.InserterDrop ?? throw new InvalidDataException("Missing native inserter drop.")))
                        && PortClear(BeltRoutePlanner.Cell(At(p,arm.InserterPickup ?? throw new InvalidDataException("Missing native inserter pickup.")))),
                    cancellationToken:token);
                return (candidates.Count,candidates.Sum(p=>Region(BeltRoutePlanner.Cell(At(p,arm.InserterPickup!)))));
            }
            bool PortClear(MapPosition p) => field.PlacementClear(belt,p,0)
                || tunnel is not null && new[] {0,4,8,12}.Any(d=>field.PlacementClear(tunnel,p,d));
            int Region(MapPosition start)
            {
                if (!routing.SurfaceClear(start)) return 0;
                var seen = new HashSet<MapPosition> {start};
                var queue = new Queue<MapPosition>(); queue.Enqueue(start);
                while (queue.TryDequeue(out var point) && seen.Count < 64)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (int direction in new[] {0,4,8,12})
                    {
                        var next = BeltRoutingField.Front(point,direction);
                        if (seen.Count < 64 && !seen.Contains(next) && routing.SurfaceClear(next)) { seen.Add(next); queue.Enqueue(next); }
                    }
                }
                return seen.Count;
            }
            // Protect the destinations with the fewest native arm positions before other routes consume their access.
            foreach (int requestIndex in remaining.OrderBy(Access))
            foreach (string sourceId in requests[requestIndex].SourceIds.Where(s => !used.Contains(s)))
            {
                if (complete) return;
                if (searches == maximumSearches) { exhausted = true; return; }
                int identity = ++searches;
                string targetId = requests[requestIndex].TargetId;
                progress?.Invoke(new(identity,selected.Count,sourceId,targetId,"started"));
                BeltTransportPlan? plan;
                try { plan = new BeltTransportPlanner().Find(current, equipment, sourceId, targetId, token, maximumBelts, nodeBudget); }
                catch (TimeoutException) { exhausted = true; progress?.Invoke(new(identity,selected.Count,sourceId,targetId,"node-budget")); continue; }
                progress?.Invoke(new(identity,selected.Count,sourceId,targetId,plan is null ? "missing" : "found",plan?.Belts.Count));
                if (plan is null) continue;
                var link = new PlannedBeltLink(sourceId, targetId, plan);
                selected.Add(link);
                used.Add(sourceId);
                Search(Project(current, equipment, link, identity), remaining.Where(i => i != requestIndex).ToArray());
                used.Remove(sourceId);
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
        for (int i = 0; i < link.Plan.Belts.Count; i++)
        {
            var part = link.Plan.Belts[i];
            var geometry = part.UndergroundType is null ? belt
                : map.Prototypes[map.Items[link.Plan.UndergroundBeltItem ?? throw new InvalidDataException("Missing underground item.")].EntityName];
            string? partner = part.UndergroundType is null ? null : $"planned:batch:{identity}:belt:{i + (part.UndergroundType == "input" ? 1 : -1)}";
            additions.Add(new($"planned:batch:{identity}:belt:{i}", geometry.Name, part.Position,
                geometry.CollisionBox.Rotate(part.Direction).Translate(part.Position), part.Direction, force,
                // This is projected occupancy, never a claim about observed engine transport lines.
                Underground: part.UndergroundType is null ? null : new(part.UndergroundType, 1, 0, partner)));
        }
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
