namespace Factorio.Agent.Core;

/// <summary>An observed enemy structure: a spawner, or a worm with its native attack range.</summary>
public sealed record NestStructure(string Id, string Name, MapPosition Position, double? Range);

/// <summary>
/// One turret-creep step: loaded turrets within native range of the target, and a stand point within build distance of
/// every turret. Exposure is the summed depth of that stand point inside worm ranges; zero means no worm reaches it.
/// </summary>
public sealed record NestAssaultStep(NestStructure Target, IReadOnlyList<PlacementCandidate> Turrets, MapPosition Stand, double Exposure);

/// <summary>
/// Plans turret creep against one locally observed nest. Gun turrets (range 18) never outrange worms (25 to 48) and the
/// character builds within 10 tiles, so a step may stand inside a worm's range: the planner minimises that depth instead
/// of forbidding it, and the caller bounds the exposure and the time spent there.
/// </summary>
public sealed class NestAssaultPlanner
{
    public const int AngleStepDegrees = 10;

    public static IReadOnlyList<NestStructure> Structures(SpatialSnapshot map)
    {
        var worms = (map.StationaryThreats ?? []).ToDictionary(t => t.Id, StringComparer.Ordinal);
        string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
        return map.Entities.Where(e => e.Force != force && e.Force != "neutral"
                && (map.Prototypes[e.Name].Type == "unit-spawner" || worms.ContainsKey(e.Id)))
            .Select(e => new NestStructure(e.Id, e.Name, e.Position, worms.TryGetValue(e.Id, out var worm) ? worm.Range : null))
            .Concat(worms.Values.Where(w => map.Entities.All(e => e.Id != w.Id))
                .Select(w => new NestStructure(w.Id, "worm", w.Position, w.Range)))
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
    }

    public NestAssaultStep? Plan(SpatialSnapshot map, string turretItem, int turretCount, CancellationToken cancellationToken = default)
    {
        if (turretCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(turretCount));
        var turret = map.Prototypes[map.Items[turretItem].EntityName];
        if (turret.Type != "ammo-turret" || turret.AttackRange is not (> 4 and < 64) || turret.TileWidth != 2 || turret.TileHeight != 2)
            throw new InvalidDataException("Turret creep requires native two-tile ammo turrets with an attack range.");
        var structures = Structures(map);
        if (structures.Count == 0) return null;
        var actor = map.Actor.Position;
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() }, 0);
        double reach = map.Actor.BuildDistance - .5;
        double range = turret.AttackRange.Value;
        var worms = structures.Where(s => s.Range is not null).ToArray();
        foreach (var target in structures.OrderBy(s => s.Position.DistanceTo(actor)).ThenBy(s => s.Id, StringComparer.Ordinal).Take(3))
        {
            NestAssaultStep? best = null;
            for (double radius = range - 3; radius >= range - 7 && radius > 2; radius -= 2)
                for (int degrees = 0; degrees < 360; degrees += AngleStepDegrees)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double angle = degrees * Math.PI / 180;
                    var center = new MapPosition(Math.Round(target.Position.X + radius * Math.Cos(angle)),
                        Math.Round(target.Position.Y + radius * Math.Sin(angle)));
                    var turrets = Block(center, turretCount).Where(p => p.DistanceTo(target.Position) <= range - 1.5
                        && field.PlacementClear(turret, p, 0)).ToArray();
                    if (turrets.Length < turretCount) continue;
                    var stand = Stand(field, turret, turrets, reach, worms, actor);
                    if (stand is null) continue;
                    var step = new NestAssaultStep(target, turrets.Select(p => new PlacementCandidate(p, 0, 0)).ToArray(), stand.Value.Position,
                        stand.Value.Exposure);
                    if (best is null || Better(step, best, actor)) best = step;
                }
            if (best is not null) return best;
        }
        return null;
    }

    private static bool Better(NestAssaultStep candidate, NestAssaultStep current, MapPosition actor) =>
        candidate.Exposure < current.Exposure - 1e-9
        || Math.Abs(candidate.Exposure - current.Exposure) <= 1e-9 && candidate.Stand.DistanceTo(actor) < current.Stand.DistanceTo(actor);

    /// <summary>Two-tile turrets around an integer corner: one, a pair, or a square of four.</summary>
    internal static IEnumerable<MapPosition> Block(MapPosition center, int count) => new MapPosition[]
    {
        new(center.X - 1, center.Y - 1), new(center.X + 1, center.Y - 1), new(center.X - 1, center.Y + 1), new(center.X + 1, center.Y + 1)
    }.Take(count);

    private static (MapPosition Position, double Exposure)? Stand(SpatialCollisionField field, EntityGeometry turret,
        IReadOnlyList<MapPosition> turrets, double reach, IReadOnlyList<NestStructure> worms, MapPosition actor)
    {
        var footprints = turrets.Select(p => turret.CollisionBox.Translate(p)).ToArray();
        var character = field.Character.CollisionBox;
        (MapPosition Position, double Exposure)? best = null;
        double minX = turrets.Min(p => p.X) - reach, maxX = turrets.Max(p => p.X) + reach;
        double minY = turrets.Min(p => p.Y) - reach, maxY = turrets.Max(p => p.Y) + reach;
        for (double x = Math.Ceiling(minX * 2) / 2; x <= maxX; x += .5)
            for (double y = Math.Ceiling(minY * 2) / 2; y <= maxY; y += .5)
            {
                var point = new MapPosition(x, y);
                if (turrets.Any(p => p.DistanceTo(point) > reach)) continue;
                var body = character.Translate(point);
                if (footprints.Any(f => f.Overlaps(new(new(body.Min.X - .3, body.Min.Y - .3), new(body.Max.X + .3, body.Max.Y + .3))))) continue;
                if (!PlacementPlanner.CanStop(field, point)) continue;
                double exposure = worms.Sum(w => Math.Max(0, w.Range!.Value - point.DistanceTo(w.Position)));
                if (best is null || exposure < best.Value.Exposure - 1e-9
                    || Math.Abs(exposure - best.Value.Exposure) <= 1e-9 && point.DistanceTo(actor) < best.Value.Position.DistanceTo(actor))
                    best = (point, exposure);
            }
        return best;
    }
}
