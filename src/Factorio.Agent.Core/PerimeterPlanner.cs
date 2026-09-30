using System.Globalization;

namespace Factorio.Agent.Core;

/// <summary>A turret and its outward wall shield. Box is the tile rectangle reserved by the complete shield.</summary>
public sealed record PerimeterNest(int Index, PlannedEntity Turret, IReadOnlyList<PlannedEntity> Walls, WorldBox Box);

/// <summary>
/// Protected is the tile rectangle of the defended industry; Ring is the outer edge of the turret line.
/// Inside and Outside are the walkable points used to prove that the actor can leave and re-enter.
/// </summary>
public sealed record PerimeterPlan(WorldBox Protected, WorldBox Ring, IReadOnlyList<PerimeterNest> Nests, double Spacing,
    int CoverageGaps, IReadOnlyList<MapPosition> Skipped, int SkippedWalls, IReadOnlyList<SpatialEntity> Clearance,
    MapPosition? Inside, MapPosition? Outside, bool CanLeave, bool CanEnter);

/// <summary>
/// Synthesizes turret nests around known industry from native geometry: turrets on a rectangle kept one walkway away
/// from the factory, neighbours no farther apart than the native range, and walls only on each nest's outward side.
/// Nests stay separated by open gaps, so the ring is never closed; a route proof on the planned field confirms it.
/// Entities of an already registered ring (<c>own</c>) are reused where the plan rebuilds them and otherwise keep the
/// same opening from every new nest, so repeating the plan never duplicates or closes the ring.
/// </summary>
public sealed class PerimeterPlanner
{
    private const int Wing = 1; // Walls overhang each turret flank by one tile against frontal attacks.
    private const int ProofMargin = 2; // Observed tiles beyond the walls where the exit proof may end.
    private static readonly (int X, int Y) North = (0, -1), East = (1, 0), South = (0, 1), West = (-1, 0);

    private sealed record Slot((int X, int Y) Nominal, IReadOnlyList<(int X, int Y)> Normals, IReadOnlyList<(int X, int Y)> Axes);

    public PerimeterPlan Plan(SpatialSnapshot map, IReadOnlyList<WorldBox> protectedBoxes, string turretItem, string wallItem,
        double range, int layers = 2, int opening = 3, IReadOnlySet<string>? own = null, CancellationToken token = default)
    {
        if (protectedBoxes.Count == 0) throw new ArgumentException("A perimeter needs known industry to protect.", nameof(protectedBoxes));
        if (layers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(layers));
        if (opening is < 3 or > 16) throw new ArgumentOutOfRangeException(nameof(opening));
        if (!double.IsFinite(range) || range <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        EntityGeometry turret = Geometry(map, turretItem), wall = Geometry(map, wallItem);
        if (wall.Type != "wall" || wall.TileWidth != 1 || wall.TileHeight != 1 || turret.TileWidth < 1 || turret.TileHeight < 1)
            throw new InvalidDataException("Perimeters require native one-tile walls and tile-sized turrets.");
        int tw = turret.TileWidth, th = turret.TileHeight, spacing = (int)Math.Floor(range);
        if (spacing < Math.Max(tw, th) + 2 * Wing + opening) throw new InvalidDataException("The native turret range is too short for open nests.");
        WorldBox factory = TileBox(Union(protectedBoxes));
        if (!map.Bounds.Contains(Outer(protectedBoxes, turret, layers, opening)))
            throw new InvalidOperationException("The perimeter exceeds the observed area; observe it from the factory centre.");
        int x0 = (int)factory.Min.X - opening - tw, y0 = (int)factory.Min.Y - opening - th;
        int x1 = (int)factory.Max.X + opening + tw, y1 = (int)factory.Max.Y + opening + th;
        var ring = new WorldBox(new(x0, y0), new(x1, y1));

        // Trees and rocks are cleared before construction, as for factory bands; everything else is a hard obstacle.
        var removable = map.Entities.Where(e => e.Id != map.Actor.Id && FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type)).ToArray();
        // The ring's own registered entities never block the slot they already fill, so a repeated plan is the same ring.
        var registered = map.Entities.Where(e => own?.Contains(e.Id) == true).ToArray();
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Except(removable).Except(registered).ToArray() });
        var nests = new List<PerimeterNest>();
        var skipped = new List<MapPosition>();
        int skippedWalls = 0, maximumShift = Math.Max(1, spacing / 4);
        foreach (var slot in Slots(x0, y0, x1, y1, tw, th, spacing))
        {
            token.ThrowIfCancellationRequested();
            PerimeterNest? accepted = null;
            foreach (var (x, y) in Shifts(slot, maximumShift))
            {
                var tiles = WallTiles(x, y, tw, th, slot.Normals, layers);
                var box = Bounds(tiles.Append((x, y)).Append((x + tw - 1, y + th - 1)));
                var center = new MapPosition(x + tw / 2.0, y + th / 2.0);
                if (nests.Any(n => Gap(n.Box, box) < opening) || !field.PlacementClear(turret, center, 0)) continue;
                if (!registered.All(e => Reproduces(e, center, tiles) || Gap(TileBox(e.Bounds), box) >= opening)) continue;
                var walls = new List<PlannedEntity>();
                foreach (var (wx, wy) in tiles)
                {
                    var at = new MapPosition(wx + .5, wy + .5);
                    if (field.PlacementClear(wall, at, 0)) walls.Add(new(WallRole(at), wallItem, at, 0));
                    else skippedWalls++;
                }
                accepted = new(nests.Count, new("turret", turretItem, center, 0), walls, box);
                break;
            }
            if (accepted is null) skipped.Add(new(slot.Nominal.X + tw / 2.0, slot.Nominal.Y + th / 2.0));
            else nests.Add(accepted);
        }

        var clearance = removable.Where(e => nests.SelectMany(Entities).Any(p => Footprint(map, p).Overlaps(e.Bounds))).ToArray();
        var access = Access(nests);
        if (!(access.Leave && access.Enter) && Access([]) is { Leave: true, Enter: true })
        {
            // Terrain can leave a single passage; a nest there loses its walls, then its turret, before closing it.
            var kept = new List<PerimeterNest>();
            foreach (var nest in nests)
            {
                if (Access([.. kept, nest]) is { Leave: true, Enter: true }) kept.Add(nest);
                else if (Access([.. kept, nest with { Walls = [] }]) is { Leave: true, Enter: true })
                {
                    kept.Add(nest with { Walls = [] });
                    skippedWalls += nest.Walls.Count;
                }
                else { skipped.Add(nest.Turret.Position); skippedWalls += nest.Walls.Count; }
            }
            nests = kept.Select((n, i) => n with { Index = i }).ToList();
            access = Access(nests);
        }
        var turrets = nests.Select(n => n.Turret.Position).ToArray();
        double worst = turrets.Length < 2 ? 0 : turrets.Select((t, i) => t.DistanceTo(turrets[(i + 1) % turrets.Length])).Max();
        int gaps = CenterLine(ring, tw, th).Count(p => !turrets.Any(t => t.DistanceTo(p) <= range));
        return new(factory, ring, nests, worst, gaps, skipped, skippedWalls, clearance, access.Inside, access.Outside, access.Leave, access.Enter);

        (bool Leave, bool Enter, MapPosition? Inside, MapPosition? Outside) Access(IReadOnlyList<PerimeterNest> planned)
        {
            token.ThrowIfCancellationRequested();
            var entities = map.Entities.Except(clearance).Concat(planned.SelectMany(Entities).Select((e, i) =>
                new SpatialEntity($"planned-perimeter-{i}", map.Items[e.Item].EntityName, e.Position, Footprint(map, e), 0, "planned"))).ToArray();
            var built = new SpatialCollisionField(map with { Entities = entities });
            var outer = new WorldBox(new(ring.Min.X - layers, ring.Min.Y - layers), new(ring.Max.X + layers, ring.Max.Y + layers));
            MapPosition? inside = Border(factory, opening / 2.0).OrderBy(p => p.DistanceTo(map.Actor.Position)).FirstOrDefault(p => built.Walkable(p));
            MapPosition? outside = inside is null ? null
                : Border(outer, 1.5).OrderBy(p => p.DistanceTo(inside)).FirstOrDefault(p => built.Walkable(p));
            if (inside is null || outside is null) return (false, false, inside, outside);
            return (Route(built, inside, outside), Route(built, outside, inside), inside, outside);
        }

        // A registered entity is part of this candidate only where the candidate would build the same entity.
        bool Reproduces(SpatialEntity entity, MapPosition center, IEnumerable<(int X, int Y)> tiles) =>
            entity.Name == turret.Name && entity.Position.DistanceTo(center) < .01
            || entity.Name == wall.Name && tiles.Any(t => entity.Position.DistanceTo(new(t.X + .5, t.Y + .5)) < .01);

        bool Route(SpatialCollisionField built, MapPosition from, MapPosition to) => new RoutePlanner().Find(
            new SpatialCollisionField(built.Map with { Actor = built.Map.Actor with { Position = from } }), to, .5, 200000,
            TimeSpan.FromSeconds(5), token).Status == RouteStatus.Found;
    }

    /// <summary>The observed tile area a perimeter around these boxes needs, including its exit proof margin.</summary>
    public static WorldBox Outer(IReadOnlyList<WorldBox> protectedBoxes, EntityGeometry turret, int layers, int opening)
    {
        var factory = TileBox(Union(protectedBoxes));
        int dx = opening + turret.TileWidth + layers + ProofMargin, dy = opening + turret.TileHeight + layers + ProofMargin;
        return new(new(factory.Min.X - dx, factory.Min.Y - dy), new(factory.Max.X + dx, factory.Max.Y + dy));
    }

    /// <summary>Grows protection from the core by the nearest optional boxes whose perimeter still fits the observation.</summary>
    public static IReadOnlyList<int> Select(SpatialSnapshot map, IReadOnlyList<WorldBox> core, IReadOnlyList<WorldBox> optional,
        string turretItem, int layers, int opening)
    {
        EntityGeometry turret = Geometry(map, turretItem);
        var chosen = core.ToList();
        bool Fits(IReadOnlyList<WorldBox> boxes) => map.Bounds.Contains(Outer(boxes, turret, layers, opening));
        if (chosen.Count > 0 && !Fits(chosen)) throw new InvalidOperationException("The factory core alone exceeds one observed perimeter.");
        var anchor = chosen.Count > 0 ? Center(Union(chosen)) : map.Actor.Position;
        var included = new List<int>();
        foreach (int index in Enumerable.Range(0, optional.Count).OrderBy(i => Center(optional[i]).DistanceTo(anchor)).ThenBy(i => i))
            if (Fits([.. chosen, optional[index]])) { chosen.Add(optional[index]); included.Add(index); }
        return included.Order().ToArray();
    }

    /// <summary>Corners first on each side, then evenly spread turrets so neighbours stay within the native range.</summary>
    private static IEnumerable<Slot> Slots(int x0, int y0, int x1, int y1, int tw, int th, int spacing)
    {
        (int X, int Y)[] corners = [(x0, y0), (x1 - tw, y0), (x1 - tw, y1 - th), (x0, y1 - th)];
        (int X, int Y)[] sides = [North, East, South, West];
        for (int side = 0; side < 4; side++)
        {
            var from = corners[side];
            var to = corners[(side + 1) % 4];
            var along = (Math.Sign(to.X - from.X), Math.Sign(to.Y - from.Y));
            var back = corners[(side + 3) % 4];
            yield return new(from, [sides[side], sides[(side + 3) % 4]], [along, (Math.Sign(back.X - from.X), Math.Sign(back.Y - from.Y))]);
            int length = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y), intervals = Math.Max(1, (length + spacing - 1) / spacing);
            for (int k = 1; k < intervals; k++)
            {
                int offset = k * length / intervals;
                yield return new((from.X + along.Item1 * offset, from.Y + along.Item2 * offset), [sides[side]], [along]);
            }
        }
    }

    private static IEnumerable<(int X, int Y)> Shifts(Slot slot, int maximum)
    {
        yield return slot.Nominal;
        for (int shift = 1; shift <= maximum; shift++)
            foreach (var axis in slot.Axes)
                foreach (int sign in new[] { 1, -1 })
                    yield return (slot.Nominal.X + axis.X * shift * sign, slot.Nominal.Y + axis.Y * shift * sign);
    }

    /// <summary>Wall tiles on the outward sides, inner layer first. Corner nests close their own corner only.</summary>
    private static IReadOnlyList<(int X, int Y)> WallTiles(int x, int y, int tw, int th, IReadOnlyList<(int X, int Y)> normals, int layers)
    {
        int Reach((int X, int Y) side) => normals.Contains(side) ? layers : Wing;
        var tiles = new HashSet<(int X, int Y)>();
        foreach (var normal in normals)
            for (int depth = 1; depth <= layers; depth++)
            {
                if (normal.Y != 0)
                {
                    int row = normal.Y < 0 ? y - depth : y + th - 1 + depth;
                    for (int column = x - Reach(West); column < x + tw + Reach(East); column++) tiles.Add((column, row));
                }
                else
                {
                    int column = normal.X < 0 ? x - depth : x + tw - 1 + depth;
                    for (int row = y - Reach(North); row < y + th + Reach(South); row++) tiles.Add((column, row));
                }
            }
        int Depth((int X, int Y) t) => Math.Max(Math.Max(x - t.X, t.X - (x + tw - 1)), Math.Max(y - t.Y, t.Y - (y + th - 1)));
        return tiles.OrderBy(Depth).ThenBy(t => t.Y).ThenBy(t => t.X).ToArray();
    }

    /// <summary>Wall roles name their tile, so a replanned wall never adopts the entity registered for another tile.</summary>
    private static string WallRole(MapPosition at) => string.Create(CultureInfo.InvariantCulture, $"wall@{at.X:0.#},{at.Y:0.#}");

    private static IEnumerable<PlannedEntity> Entities(PerimeterNest nest) => nest.Walls.Prepend(nest.Turret);

    private static WorldBox Footprint(SpatialSnapshot map, PlannedEntity entity) =>
        map.Prototypes[map.Items[entity.Item].EntityName].CollisionBox.Rotate(entity.Direction).Translate(entity.Position);

    private static IEnumerable<MapPosition> CenterLine(WorldBox ring, int tw, int th)
    {
        var line = new WorldBox(new(ring.Min.X + tw / 2.0, ring.Min.Y + th / 2.0), new(ring.Max.X - tw / 2.0, ring.Max.Y - th / 2.0));
        return Border(line, 0);
    }

    /// <summary>Points one tile apart on the rectangle lying the given distance outside a box.</summary>
    private static IEnumerable<MapPosition> Border(WorldBox box, double distance)
    {
        double left = box.Min.X - distance, top = box.Min.Y - distance, right = box.Max.X + distance, bottom = box.Max.Y + distance;
        for (double x = left; x <= right; x++) { yield return new(x, top); yield return new(x, bottom); }
        for (double y = top + 1; y < bottom; y++) { yield return new(left, y); yield return new(right, y); }
    }

    private static WorldBox Bounds(IEnumerable<(int X, int Y)> tiles)
    {
        var all = tiles.ToArray();
        return new(new(all.Min(t => t.X), all.Min(t => t.Y)), new(all.Max(t => t.X) + 1, all.Max(t => t.Y) + 1));
    }

    private static WorldBox Union(IReadOnlyList<WorldBox> boxes) => new(new(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y)),
        new(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y)));

    private static WorldBox TileBox(WorldBox box) => new(new(Math.Floor(box.Min.X), Math.Floor(box.Min.Y)), new(Math.Ceiling(box.Max.X), Math.Ceiling(box.Max.Y)));

    private static MapPosition Center(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);

    /// <summary>Chebyshev tile gap between two rectangles; zero or less when they touch or overlap.</summary>
    private static double Gap(WorldBox a, WorldBox b) =>
        Math.Max(Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X), Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));
}
