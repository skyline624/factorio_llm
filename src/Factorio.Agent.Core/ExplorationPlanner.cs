namespace Factorio.Agent.Core;

public sealed class ExplorationBlockedException(string message) : InvalidOperationException(message);

/// <summary>Every remaining exploration target or step enters a recent death zone or the margin of a known stationary threat.</summary>
public sealed class ExplorationDangerException(string message) : InvalidOperationException(message);

/// <summary>
/// Local frontier selection. Only observed terrain, historical solid resources and stationary threats seen during this search
/// are remembered. Targets, steps and routes keep away from worms by <see cref="ThreatMargin"/> beyond their range,
/// including travel to a caller's destination. Recent death zones apply when supplied by the caller.
/// </summary>
public sealed class ExplorationPlanner
{
    /// <summary>Alternative blind frontiers examined against one unchanged local collision map.</summary>
    public const int MaximumFrontierSelections = 16;
    /// <summary>
    /// Much wider than the two-tile routing margin: base biters see 30 tiles and nests stand beside worms. On 2026-10-01
    /// (seed 20261002) exploration steps died 37 to 38 tiles from visible range-25 worms.
    /// </summary>
    public const double ThreatMargin = 24;
    /// <summary>A frontier nearer than this to a hazard boundary costs up to as many extra tiles of walking.</summary>
    public const double PreferredClearance = 32;
    private readonly HashSet<(int X, int Y)> observed = [];
    private readonly Dictionary<string, SpatialEntity> resources = [];
    private readonly Dictionary<string, StationaryThreat> threats = [];
    private readonly Dictionary<(int X, int Y), int> visits = [];
    private readonly Dictionary<(int X, int Y), int> frontierAttempts = [];
    private MapPosition? origin;
    private MapPosition? frontierGoal;
    private double frontierDistance;
    private int stalledFrontierSteps;

    public MapPosition? Frontier => frontierGoal;

    /// <param name="deaths">Active death zones while exploring; null when <paramref name="destination"/> is the caller's own choice.</param>
    public MapPosition Choose(SpatialSnapshot map, string wanted, ProductionCatalog catalog, MapPosition? destination = null,
        IReadOnlyList<SurveyedCell>? surveyed = null, IReadOnlyList<NativeDeathTransition>? deaths = null,
        IReadOnlySet<string>? deferredResourceIds = null)
    {
        origin ??= map.Actor.Position;
        foreach (var cell in surveyed ?? []) observed.Add((cell.X, cell.Y));
        foreach (string id in resources.Where(p => map.Bounds.Contains(p.Value.Position)).Select(p => p.Key).ToArray())
            resources.Remove(id);
        foreach (SpatialEntity entity in map.Entities.Where(e => catalog.Mining.ContainsKey(e.Name)))
            resources[entity.Id] = entity;
        // A remembered worm absent from a current view of its position no longer exists.
        foreach (string id in threats.Where(p => map.Bounds.Contains(p.Value.Position)).Select(p => p.Key).ToArray())
            threats.Remove(id);
        foreach (StationaryThreat threat in map.StationaryThreats ?? []) threats[threat.Id] = threat;
        for (int x = (int)Math.Ceiling(map.Bounds.Min.X / 4); x < map.Bounds.Max.X / 4; x++)
            for (int y = (int)Math.Ceiling(map.Bounds.Min.Y / 4); y < map.Bounds.Max.Y / 4; y++) observed.Add((x, y));
        bool exploring = deaths is not null;
        bool InDeathZone(MapPosition p) => exploring && deaths!.Any(d => DangerZones.Covers(d, p));
        bool NearThreat(MapPosition p) => threats.Values.Any(t => p.DistanceTo(t.Position) <= t.Range + ThreatMargin);
        MapPosition? known = destination ?? resources.Values.Where(e => catalog.Mining[e.Name].Any(p => p.Name == wanted && p.DeterministicItem)
                && !InDeathZone(e.Position) && deferredResourceIds?.Contains(e.Id) != true)
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).Select(e => e.Position).FirstOrDefault();
        var field = new SpatialCollisionField(map, ThreatMargin);
        if (known is not null) frontierGoal = null;
        else if (frontierGoal is not null)
        {
            double distance = frontierGoal.DistanceTo(map.Actor.Position);
            stalledFrontierSteps = frontierDistance - distance < 1 ? stalledFrontierSteps + 1 : 0;
            frontierDistance = distance;
            if (distance > 8 && stalledFrontierSteps < 4 && !InDeathZone(frontierGoal) && !NearThreat(frontierGoal)
                && (!map.Bounds.Contains(frontierGoal) || field.Walkable(frontierGoal))) known = frontierGoal;
            else frontierGoal = null;
        }
        bool towardFrontier = known == frontierGoal;
        var candidates = new List<(MapPosition Point, (int, int) Cell, int Gain)>();
        var unreachable = new HashSet<(int, int)>();
        int hazardous = 0;
        for (int x = (int)Math.Ceiling(map.Bounds.Min.X / 4) + 1; x < map.Bounds.Max.X / 4 - 1; x++)
            for (int y = (int)Math.Ceiling(map.Bounds.Min.Y / 4) + 1; y < map.Bounds.Max.Y / 4 - 1; y++)
            {
                var point = new MapPosition(x * 4, y * 4);
                double distance = point.DistanceTo(map.Actor.Position);
                // Each exploration step hands control back over the network. Belts can be crossed,
                // but the endpoint must remain still between its native receipt and the next observation.
                if (distance is < 16 or > 28 || !PlacementPlanner.CanStop(field, point)) continue;
                if (exploring && deaths!.Any(d => Enters(map, point, d.Position, DangerZones.Radius))
                    || threats.Values.Any(t => Enters(map, point, t.Position, t.Range + ThreatMargin)))
                {
                    hazardous++;
                    continue;
                }
                int gain = 0;
                for (int dx = -7; dx <= 7; dx++)
                    for (int dy = -7; dy <= 7; dy++) if (!observed.Contains((x + dx, y + dy))) gain++;
                candidates.Add((point, (x, y), gain));
            }
        if (candidates.Count == 0 && hazardous > 0)
            throw new ExplorationDangerException($"All {hazardous} local exploration steps enter a recent death zone or "
                + $"{ThreatMargin} tiles beyond a known stationary threat's range.");

        bool Progresses((MapPosition Point, (int, int) Cell, int Gain) candidate, MapPosition target) => candidate.Gain > 0
            || candidate.Point.DistanceTo(target) < map.Actor.Position.DistanceTo(target) - 1;

        for (int selection = 0; selection < MaximumFrontierSelections; selection++)
        {
            if (known is null)
            {
                var frontier = new HashSet<(int X, int Y)>();
                foreach (var cell in observed)
                    foreach (var neighbor in new[] { (cell.X + 1, cell.Y), (cell.X - 1, cell.Y), (cell.X, cell.Y + 1), (cell.X, cell.Y - 1) })
                        if (!observed.Contains(neighbor) && frontierAttempts.GetValueOrDefault(neighbor) < 4) frontier.Add(neighbor);
                if (frontier.Count == 0) throw new InvalidOperationException("Exploration exhausted its attempted frontiers.");
                var safe = frontier.Where(p => !InDeathZone(Center(p)) && !NearThreat(Center(p))).ToArray();
                if (safe.Length == 0)
                    throw new ExplorationDangerException($"All {frontier.Count} exploration frontiers lie within a recent death zone or "
                        + $"{ThreatMargin} tiles beyond a known stationary threat's range; this local refusal does not prove the resource absent.");
                // Prefer nearby frontiers while retaining a modest home-distance cost. Pure nearest
                // selection drifts along one axis when tile rounding makes that border slightly nearer.
                // Frontiers just outside a hazard cost extra walking, so farther safe ones can win.
                // Many consecutive shoreline cells can fail the same geometric approach.
                // They must not consume the bounded route attempts before a farther dry exit is considered.
                var ranked = safe.OrderBy(p => Center(p).DistanceTo(map.Actor.Position) + 0.25 * Center(p).DistanceTo(origin)
                        + Math.Max(0, PreferredClearance - Clearance(Center(p))))
                    .ThenBy(p => Center(p).DistanceTo(origin)).ThenBy(p => p.Y).ThenBy(p => p.X).ToArray();
                (int X, int Y)? selected = null;
                foreach (var cell in ranked)
                {
                    if (candidates.Any(c => !unreachable.Contains(c.Cell) && Progresses(c, Center(cell))))
                    {
                        selected = cell;
                        break;
                    }
                    // Keep this local refusal for the current search, as with a failed route,
                    // so adjacent shoreline cells do not repeatedly reverse the next walk.
                    frontierAttempts[cell] = 4;
                }
                if (selected is null)
                    throw new ExplorationBlockedException("No locally progressing step toward a safe frontier in the current collision map.");
                known = Center(selected.Value);
                frontierGoal = known;
                frontierDistance = known.DistanceTo(map.Actor.Position);
                stalledFrontierSteps = 0;
                frontierAttempts[selected.Value] = frontierAttempts.GetValueOrDefault(selected.Value) + 1;
            }
            bool searchBudgetExceeded = false;
            foreach (var candidate in candidates.Where(c => !unreachable.Contains(c.Cell))
                .OrderByDescending(c => -c.Point.DistanceTo(known) * 5 + c.Gain * 0.1 - visits.GetValueOrDefault(c.Cell) * 100)
                .ThenBy(c => c.Point.Y).ThenBy(c => c.Point.X))
            {
                // A blind frontier beyond a shoreline must not keep the actor pacing its bank.
                // An ordinary step either approaches that frontier or observes some new terrain.
                // A caller's destination retains its existing detours and is never replaced here.
                if (towardFrontier && !Progresses(candidate, known)) continue;
                RoutePlan route = new RoutePlanner().Find(field, candidate.Point);
                if (route.Status != RouteStatus.Found)
                {
                    searchBudgetExceeded |= route.Status == RouteStatus.BudgetExceeded;
                    if (route.Status != RouteStatus.BudgetExceeded) unreachable.Add(candidate.Cell);
                    continue;
                }
                visits[candidate.Cell] = visits.GetValueOrDefault(candidate.Cell) + 1;
                return candidate.Point;
            }
            if (searchBudgetExceeded) throw new InvalidOperationException("Exploration route search exceeded its budget; reachability remains unknown.");
            if (towardFrontier && candidates.Count > 0)
            {
                // This local view has disproved the approach. Reuse its geometry for another
                // frontier instead of spending four further native walks on the same one.
                frontierAttempts[((int)known!.X / 4, (int)known.Y / 4)] = 4;
                frontierGoal = known = null;
                continue;
            }
            throw new ExplorationBlockedException("No reachable exploration frontier in the current collision map.");
        }
        throw new InvalidOperationException($"Exploration examined {MaximumFrontierSelections} alternative frontiers without a progressing local step; reachability remains unknown.");

        // Distance from a point to the nearest hazard boundary; negative inside.
        double Clearance(MapPosition p) => !exploring ? double.PositiveInfinity
            : deaths!.Select(d => p.DistanceTo(d.Position) - DangerZones.Radius)
                .Concat(threats.Values.Select(t => p.DistanceTo(t.Position) - t.Range - ThreatMargin))
                .DefaultIfEmpty(double.PositiveInfinity).Min();
    }

    private static MapPosition Center((int X, int Y) cell) => new(cell.X * 4, cell.Y * 4);

    // A hazard may be left, never entered: an actor already inside may only step farther from its centre.
    private static bool Enters(SpatialSnapshot map, MapPosition point, MapPosition centre, double radius)
    {
        double from = map.Actor.Position.DistanceTo(centre), to = point.DistanceTo(centre);
        return to <= radius && !(from <= radius && to > from);
    }
}
