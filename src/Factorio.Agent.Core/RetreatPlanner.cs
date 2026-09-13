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
    public static bool Needed(SafetyObservation state) => state.Alive && state.ControlMode == "ai" && !state.StopUnconfirmed
        && state.Health > 0 && state.Position is not null && state.LocalEnemiesComplete && state.Enemies.Count > 0
        && (state.MaxHealth is { } maximum && state.Health <= maximum * .4
            || !state.Weapon.Ready && EquipmentPolicy.Select(state) is null);

    public RetreatPlan Find(SafetyObservation state, SpatialSnapshot map, CancellationToken token = default)
    {
        if (!Needed(state)) return new("not-needed");
        if (map.Scope != state.Scope || map.CollectedTick < state.Tick || map.CollectedTick - state.Tick > 60
            || map.Actor.ControlMode != "ai" || map.Actor.Position.DistanceTo(state.Position!) > .5)
            throw new InvalidDataException("Retreat geometry no longer matches the current native safety observation.");
        var field = new SpatialCollisionField(map);
        var start = map.Actor.Position;
        double Separation(MapPosition point) => state.Enemies.Min(e => point.DistanceTo(e.Position));
        double initialSeparation = Separation(start);
        var candidates = new List<(string? RefugeId, MapPosition Destination, double Cost)>();
        foreach (var refuge in state.Defenses ?? [])
        {
            var entity = map.Entities.SingleOrDefault(e => e.Id == refuge.Id);
            if (entity is null || map.Prototypes[entity.Name].Type != "ammo-turret" || entity.Position != refuge.Position) continue;
            double radius = Math.Min(refuge.Range * .5, 12);
            for (int x = (int)Math.Ceiling(refuge.Position.X - radius); x <= Math.Floor(refuge.Position.X + radius); x++)
                for (int y = (int)Math.Ceiling(refuge.Position.Y - radius); y <= Math.Floor(refuge.Position.Y + radius); y++)
                {
                    var point = new MapPosition(x, y);
                    if (point.DistanceTo(refuge.Position) > radius || start.DistanceTo(point) > 32
                        || Separation(point) < initialSeparation + 2 || !field.Walkable(point)) continue;
                    candidates.Add((refuge.Id, point, start.DistanceTo(point) + point.DistanceTo(refuge.Position) * .5));
                }
        }
        if (state.Defenses is not { Count: > 0 })
        {
            // A local escape is useful even without turret coverage. It proves increased
            // separation on observed terrain, not safety from pursuit or hidden enemies.
            for (int x = (int)Math.Ceiling(start.X - 12); x <= Math.Floor(start.X + 12); x++)
                for (int y = (int)Math.Ceiling(start.Y - 12); y <= Math.Floor(start.Y + 12); y++)
                {
                    var point = new MapPosition(x, y);
                    double distance = start.DistanceTo(point), gain = Separation(point) - initialSeparation;
                    if (distance is < 4 or > 12 || gain < 2 || !field.Walkable(point)) continue;
                    candidates.Add((null, point, distance - 2 * gain));
                }
        }
        long began = Stopwatch.GetTimestamp();
        int attempted = 0;
        bool limited = false;
        foreach (var candidate in candidates.OrderBy(c => c.Cost).ThenBy(c => c.RefugeId, StringComparer.Ordinal)
            .ThenBy(c => c.Destination.X).ThenBy(c => c.Destination.Y))
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

    private static double SegmentDistance(MapPosition point, MapPosition from, MapPosition to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        double ratio = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        return point.DistanceTo(new(from.X + dx * ratio, from.Y + dy * ratio));
    }
}
