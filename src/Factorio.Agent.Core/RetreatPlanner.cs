using System.Diagnostics;
using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record DefensiveRefuge(string Id, MapPosition Position, double Range, long AmmoRounds, long CollectedTick)
{
    public static IReadOnlyList<DefensiveRefuge> Read(JsonElement node, long tick)
    {
        if (node.ValueKind == JsonValueKind.Object && !node.EnumerateObject().Any()) return [];
        var result = node.Deserialize<DefensiveRefuge[]>(Protocol.Json) ?? throw new InvalidDataException("Missing defensive refuge list.");
        if (result.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != result.Length
            || result.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Position is null || !double.IsFinite(r.Position.X)
                || !double.IsFinite(r.Position.Y) || !double.IsFinite(r.Range) || r.Range <= 0 || r.AmmoRounds <= 0 || r.CollectedTick != tick))
            throw new InvalidDataException("Invalid or stale loaded-turret observation.");
        return result;
    }
}

public sealed record RetreatPlan(string Status, string? RefugeId = null, MapPosition? Destination = null,
    MapPosition? Next = null, RoutePlan? Route = null);

/// <summary>Bounded local route toward observed ammunition coverage; never a guarantee of escaping faster enemies.</summary>
public sealed class RetreatPlanner
{
    /// <summary>Visible enemies from which the actor stops trading health for kills.</summary>
    public const int OutnumberedEnemies = 3;
    /// <summary>Route attempts toward turret refuges before local escapes are tried.</summary>
    public const int RefugeAttempts = 6;

    /// <summary>
    /// Retreat at critical health, when unarmed, or when outnumbered and already hurt: on 2026-10-01 (seed 20261002) a pack
    /// took the actor from above 40% to 27% health within one observation and it died on its way to a turret.
    /// </summary>
    public static bool Needed(SafetyObservation state) => state.Alive && state.ControlMode == "ai" && !state.StopUnconfirmed
        && state.Health > 0 && state.Position is not null && state.LocalEnemiesComplete && state.Enemies.Count > 0
        && (state.MaxHealth is { } maximum && (state.Health <= maximum * .4
                || state.Enemies.Count >= OutnumberedEnemies && state.Health <= maximum * .75)
            || !state.Weapon.Ready && EquipmentPolicy.Select(state) is null);

    /// <summary>
    /// Outnumbered but still healthy beside an observed loaded turret it is not yet covered by: reach the turret before fighting.
    /// On 2026-10-01 (seed 20261002) the pistol-armed actor went from full health to death in about seven seconds against
    /// packs; once hurt, the biters outran its retreat. Only turret refuges are tried in this case: a local escape does not
    /// outrun biters, so without a reachable refuge the actor keeps fighting.
    /// </summary>
    public static bool SeeksCover(SafetyObservation state) => state.Alive && state.ControlMode == "ai" && !state.StopUnconfirmed
        && state.Health > 0 && state.Position is { } position && state.LocalEnemiesComplete && state.Enemies.Count >= OutnumberedEnemies
        && state.Defenses is { Count: > 0 } refuges && !refuges.Any(r => position.DistanceTo(r.Position) <= CoverRadius(r));

    /// <summary>Destinations lie in the inner half of a turret's native range, at most twelve tiles from it.</summary>
    public static double CoverRadius(DefensiveRefuge refuge) => Math.Min(refuge.Range * .5, 12);

    public RetreatPlan Find(SafetyObservation state, SpatialSnapshot map, CancellationToken token = default)
    {
        bool needed = Needed(state);
        if (!needed && !SeeksCover(state)) return new("not-needed");
        if (map.Scope != state.Scope || map.CollectedTick < state.Tick || map.Actor.ControlMode != "ai")
            throw new InvalidDataException("Retreat geometry no longer matches the current native safety observation.");
        // A running movement can advance between the two read-only captures; wait for another safety step.
        if (map.CollectedTick - state.Tick > 60 || map.Actor.Position.DistanceTo(state.Position!) > .5)
            return new("observation-changed");
        var field = new SpatialCollisionField(map);
        // Destinations cut off by walls or water would spend the route budget on searches that cannot succeed.
        var reachable = Reachable(field, map.Actor.Position, 32);
        var start = map.Actor.Position;
        double Separation(MapPosition point) => state.Enemies.Min(e => point.DistanceTo(e.Position));
        double initialSeparation = Separation(start);
        var candidates = new List<(string? RefugeId, MapPosition Destination, double Cost)>();
        foreach (var refuge in state.Defenses ?? [])
        {
            var entity = map.Entities.SingleOrDefault(e => e.Id == refuge.Id);
            if (entity is null || map.Prototypes[entity.Name].Type != "ammo-turret" || entity.Position != refuge.Position) continue;
            double radius = CoverRadius(refuge);
            for (int x = (int)Math.Ceiling(refuge.Position.X - radius); x <= Math.Floor(refuge.Position.X + radius); x++)
                for (int y = (int)Math.Ceiling(refuge.Position.Y - radius); y <= Math.Floor(refuge.Position.Y + radius); y++)
                {
                    var point = new MapPosition(x, y);
                    if (point.DistanceTo(refuge.Position) > radius || start.DistanceTo(point) > 32 || !reachable.Contains((x, y))
                        || Separation(point) < initialSeparation + 2 || !field.Walkable(point)) continue;
                    candidates.Add((refuge.Id, point, start.DistanceTo(point) + point.DistanceTo(refuge.Position) * .5));
                }
        }
        // A local escape is useful without turret coverage and when no observed turret can be reached in time. It proves
        // increased separation on observed terrain, not safety from pursuit or hidden enemies. On 2026-10-01 (seed 20261002)
        // escapes were only tried when no turret existed anywhere: routes to distant turrets spent the budget and the actor died.
        // A healthy actor that only seeks cover never escapes: biters outrun it, so it fights instead.
        var escapes = new List<(string? RefugeId, MapPosition Destination, double Cost)>();
        for (int x = (int)Math.Ceiling(start.X - 12); needed && x <= Math.Floor(start.X + 12); x++)
            for (int y = (int)Math.Ceiling(start.Y - 12); y <= Math.Floor(start.Y + 12); y++)
            {
                var point = new MapPosition(x, y);
                double distance = start.DistanceTo(point), gain = Separation(point) - initialSeparation;
                if (distance is < 4 or > 12 || gain < 2 || !reachable.Contains((x, y))) continue;
                escapes.Add((null, point, distance - 2 * gain));
            }
        long began = Stopwatch.GetTimestamp();
        int attempted = 0;
        bool limited = false;
        // Turret refuges come first but may only use half the attempts, so a local escape is always tried in time.
        var ordered = candidates.OrderBy(c => c.Cost).ThenBy(c => c.RefugeId, StringComparer.Ordinal)
            .ThenBy(c => c.Destination.X).ThenBy(c => c.Destination.Y).Take(RefugeAttempts)
            .Concat(escapes.OrderBy(c => c.Cost).ThenBy(c => c.Destination.X).ThenBy(c => c.Destination.Y));
        foreach (var candidate in ordered)
        {
            if (++attempted > 12 || Stopwatch.GetElapsedTime(began) > TimeSpan.FromMilliseconds(200)) return new("search-budget");
            var route = new RoutePlanner().Find(field, candidate.Destination, maximumNodes: 5000,
                timeBudget: TimeSpan.FromMilliseconds(20), token: token);
            if (route.Status == RouteStatus.BudgetExceeded) limited = true;
            if (route.Status != RouteStatus.Found || route.Waypoints.Count == 0 || route.Length > 40) continue;
            var previous = start;
            bool avoidsThreat = true;
            foreach (var point in route.Waypoints)
            {
                if (state.Enemies.Any(e => SegmentDistance(e.Position, previous, point) < Math.Max(0, initialSeparation - .5)))
                { avoidsThreat = false; break; }
                previous = point;
            }
            if (!avoidsThreat) continue;
            var first = route.Waypoints[0];
            double distance = start.DistanceTo(first);
            var next = distance <= 2 ? first : new MapPosition(start.X + (first.X - start.X) * 2 / distance,
                start.Y + (first.Y - start.Y) * 2 / distance);
            double clearance = route.UsesTightStartConnector ? 0 : .18;
            if (distance < .1 || !field.SteeringRegionClear(start, next, clearance)) continue;
            return new(candidate.RefugeId is null ? "separation" : "found", candidate.RefugeId, candidate.Destination, next, route);
        }
        return new(limited ? "search-budget" : "no-safe-candidate");
    }

    /// <summary>
    /// Integer points within the radius connected to the actor through walkable points of the observed field: a cheap
    /// flood fill that tells which refuges and escapes a route can reach at all.
    /// </summary>
    internal static HashSet<(int X, int Y)> Reachable(SpatialCollisionField field, MapPosition start, int radius)
    {
        int ox = (int)Math.Round(start.X), oy = (int)Math.Round(start.Y);
        var reached = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();
        // The actor may stand beside an entity: any walkable point next to it starts the fill.
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (field.Walkable(new MapPosition(ox + dx, oy + dy)) && reached.Add((ox + dx, oy + dy))) queue.Enqueue((ox + dx, oy + dy));
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (Math.Abs(nx - ox) > radius || Math.Abs(ny - oy) > radius || reached.Contains((nx, ny))
                    || !field.Walkable(new MapPosition(nx, ny))) continue;
                reached.Add((nx, ny));
                queue.Enqueue((nx, ny));
            }
        }
        return reached;
    }

    private static double SegmentDistance(MapPosition point, MapPosition from, MapPosition to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        double ratio = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        return point.DistanceTo(new(from.X + dx * ratio, from.Y + dy * ratio));
    }
}
