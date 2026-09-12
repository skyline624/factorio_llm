namespace Factorio.Agent.Core;

/// <summary>Local frontier selection. Only observed terrain and historical solid resources are remembered.</summary>
public sealed class ExplorationPlanner
{
    private readonly HashSet<(int X, int Y)> observed = [];
    private readonly Dictionary<string, SpatialEntity> resources = [];
    private readonly Dictionary<(int X, int Y), int> visits = [];
    private readonly Dictionary<(int X, int Y), int> frontierAttempts = [];
    private MapPosition? origin;
    private MapPosition? frontierGoal;
    private double frontierDistance;
    private int stalledFrontierSteps;

    public MapPosition Choose(SpatialSnapshot map, string wanted, ProductionCatalog catalog, MapPosition? destination = null)
    {
        origin ??= map.Actor.Position;
        foreach (string id in resources.Where(p => map.Bounds.Contains(p.Value.Position)).Select(p => p.Key).ToArray())
            resources.Remove(id);
        foreach (SpatialEntity entity in map.Entities.Where(e => catalog.Mining.ContainsKey(e.Name)))
            resources[entity.Id] = entity;
        for (int x = (int)Math.Ceiling(map.Bounds.Min.X / 4); x < map.Bounds.Max.X / 4; x++)
            for (int y = (int)Math.Ceiling(map.Bounds.Min.Y / 4); y < map.Bounds.Max.Y / 4; y++) observed.Add((x, y));
        MapPosition? known = destination ?? resources.Values.Where(e => catalog.Mining[e.Name].Any(p => p.Name == wanted && p.DeterministicItem))
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).Select(e => e.Position).FirstOrDefault();
        var field = new SpatialCollisionField(map);
        if (known is not null) frontierGoal = null;
        else if (frontierGoal is not null)
        {
            double distance = frontierGoal.DistanceTo(map.Actor.Position);
            stalledFrontierSteps = frontierDistance - distance < 1 ? stalledFrontierSteps + 1 : 0;
            frontierDistance = distance;
            if (distance > 8 && stalledFrontierSteps < 4
                && (!map.Bounds.Contains(frontierGoal) || field.Walkable(frontierGoal))) known = frontierGoal;
            else frontierGoal = null;
        }
        if (known is null)
        {
            var frontier = new HashSet<(int X, int Y)>();
            foreach (var cell in observed)
                foreach (var neighbor in new[] { (cell.X + 1, cell.Y), (cell.X - 1, cell.Y), (cell.X, cell.Y + 1), (cell.X, cell.Y - 1) })
                    if (!observed.Contains(neighbor) && frontierAttempts.GetValueOrDefault(neighbor) < 4) frontier.Add(neighbor);
            // Prefer nearby frontiers while retaining a modest home-distance cost. Pure nearest
            // selection drifts along one axis when tile rounding makes that border slightly nearer.
            var selected = frontier.OrderBy(p => new MapPosition(p.X * 4, p.Y * 4).DistanceTo(map.Actor.Position)
                    + 0.25 * new MapPosition(p.X * 4, p.Y * 4).DistanceTo(origin))
                .ThenBy(p => new MapPosition(p.X * 4, p.Y * 4).DistanceTo(origin)).ThenBy(p => p.Y).ThenBy(p => p.X).FirstOrDefault();
            if (frontier.Count == 0) throw new InvalidOperationException("Exploration exhausted its attempted frontiers.");
            known = new(selected.X * 4, selected.Y * 4);
            frontierGoal = known;
            frontierDistance = known.DistanceTo(map.Actor.Position);
            stalledFrontierSteps = 0;
            frontierAttempts[selected] = frontierAttempts.GetValueOrDefault(selected) + 1;
        }
        var candidates = new List<(MapPosition Point, double Score, (int, int) Cell)>();
        for (int x = (int)Math.Ceiling(map.Bounds.Min.X / 4) + 1; x < map.Bounds.Max.X / 4 - 1; x++)
            for (int y = (int)Math.Ceiling(map.Bounds.Min.Y / 4) + 1; y < map.Bounds.Max.Y / 4 - 1; y++)
            {
                var point = new MapPosition(x * 4, y * 4);
                double distance = point.DistanceTo(map.Actor.Position);
                if (distance is < 16 or > 28 || !field.Walkable(point)) continue;
                int gain = 0;
                for (int dx = -7; dx <= 7; dx++)
                    for (int dy = -7; dy <= 7; dy++) if (!observed.Contains((x + dx, y + dy))) gain++;
                double score = known is null ? gain - distance * 0.1 : -point.DistanceTo(known) * 5 + gain * 0.1;
                candidates.Add((point, score - visits.GetValueOrDefault((x, y)) * 100, (x, y)));
            }
        foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Point.Y).ThenBy(c => c.Point.X))
        {
            RoutePlan route = new RoutePlanner().Find(field, candidate.Point);
            if (route.Status != RouteStatus.Found) continue;
            visits[candidate.Cell] = visits.GetValueOrDefault(candidate.Cell) + 1;
            return candidate.Point;
        }
        throw new InvalidOperationException("No reachable exploration frontier in the current collision map.");
    }
}
