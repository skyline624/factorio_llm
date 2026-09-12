namespace Factorio.Agent.Core;

/// <summary>Native masks and boxes indexed by tile. Unknown space is never assumed clear.</summary>
public sealed class SpatialCollisionField
{
    private sealed record Obstacle(string Id, WorldBox Bounds, CollisionMask Mask, bool Tile);
    private readonly Dictionary<(int X, int Y), List<Obstacle>> buckets = [];
    public SpatialSnapshot Map { get; }
    public EntityGeometry Character => Map.Prototypes[Map.Actor.Name];

    public SpatialCollisionField(SpatialSnapshot map)
    {
        Map = map;
        var covered = new HashSet<(int X, int Y)>();
        foreach (TileRun row in map.Rows)
        {
            if (row.Length <= 0 || !map.TilePrototypes.TryGetValue(row.Name, out CollisionMask? mask))
                throw new InvalidDataException("Invalid native terrain run.");
            for (int x = row.X; x < row.X + row.Length; x++)
            {
                var box = new WorldBox(new(x, row.Y), new(x + 1, row.Y + 1));
                if (!map.Bounds.Contains(box) || !covered.Add((x, row.Y)))
                    throw new InvalidDataException("Repeated or out-of-bounds terrain tile.");
                Add(new($"tile:{x}:{row.Y}", box, mask, true));
            }
        }
        if (covered.Count != map.Bounds.Width * map.Bounds.Height)
            throw new InvalidDataException("Terrain coverage has holes.");
        foreach (SpatialEntity entity in map.Entities)
            Add(new(entity.Id, entity.Bounds, map.Prototypes[entity.Name].Mask, false));
    }

    private void Add(Obstacle obstacle)
    {
        if (obstacle.Mask.Layers.Count == 0 || obstacle.Bounds.Width == 0 || obstacle.Bounds.Height == 0) return;
        for (int x = (int)Math.Floor(obstacle.Bounds.Min.X); x <= Math.Floor(obstacle.Bounds.Max.X); x++)
            for (int y = (int)Math.Floor(obstacle.Bounds.Min.Y); y <= Math.Floor(obstacle.Bounds.Max.Y); y++)
            {
                if (!buckets.TryGetValue((x, y), out List<Obstacle>? list)) buckets[(x, y)] = list = [];
                list.Add(obstacle);
            }
    }

    private IEnumerable<Obstacle> Query(WorldBox box)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int x = (int)Math.Floor(box.Min.X); x <= Math.Floor(box.Max.X); x++)
            for (int y = (int)Math.Floor(box.Min.Y); y <= Math.Floor(box.Max.Y); y++)
                if (buckets.TryGetValue((x, y), out List<Obstacle>? list))
                    foreach (Obstacle obstacle in list)
                        if (seen.Add(obstacle.Id)) yield return obstacle;
    }

    public bool Walkable(MapPosition position, double clearance = 0.18) => SegmentClear(position, position, clearance);

    public bool SegmentClear(MapPosition from, MapPosition to, double clearance = 0.18)
    {
        if (!double.IsFinite(clearance) || clearance < 0) throw new ArgumentOutOfRangeException(nameof(clearance));
        WorldBox body = Character.CollisionBox;
        var swept = new WorldBox(new(Math.Min(from.X, to.X) + body.Min.X - clearance, Math.Min(from.Y, to.Y) + body.Min.Y - clearance),
            new(Math.Max(from.X, to.X) + body.Max.X + clearance, Math.Max(from.Y, to.Y) + body.Max.Y + clearance));
        if (!Map.Bounds.Contains(swept)) return false;
        foreach (Obstacle obstacle in Query(swept))
        {
            if (obstacle.Id == Map.Actor.Id || !Character.Mask.CollidesWith(obstacle.Mask, obstacle.Tile)) continue;
            WorldBox footprint = obstacle.Tile && Character.Mask.TileTransitions ? new(new(0, 0), new(0, 0)) : body;
            var expanded = new WorldBox(new(obstacle.Bounds.Min.X - footprint.Max.X - clearance, obstacle.Bounds.Min.Y - footprint.Max.Y - clearance),
                new(obstacle.Bounds.Max.X - footprint.Min.X + clearance, obstacle.Bounds.Max.Y - footprint.Min.Y + clearance));
            // Contact without penetration is legal. This also permits moving away after a native collision stop.
            const double contactEpsilon = 1e-7;
            expanded = new(new(expanded.Min.X + contactEpsilon, expanded.Min.Y + contactEpsilon),
                new(expanded.Max.X - contactEpsilon, expanded.Max.Y - contactEpsilon));
            if (IntersectsSegment(expanded, from, to)) return false;
        }
        return true;
    }

    public bool PlacementClear(EntityGeometry geometry, MapPosition position, int direction)
    {
        WorldBox box = geometry.CollisionBox.Rotate(direction).Translate(position);
        if (!Map.Bounds.Contains(box)) return false;
        return !Query(box).Any(o => geometry.Mask.CollidesWith(o.Mask, o.Tile) && box.Overlaps(o.Bounds));
    }

    private static bool IntersectsSegment(WorldBox box, MapPosition from, MapPosition to)
    {
        double low = 0, high = 1;
        return Slab(from.X, to.X - from.X, box.Min.X, box.Max.X, ref low, ref high)
            && Slab(from.Y, to.Y - from.Y, box.Min.Y, box.Max.Y, ref low, ref high);
    }

    private static bool Slab(double origin, double delta, double minimum, double maximum, ref double low, ref double high)
    {
        if (Math.Abs(delta) < 1e-12) return origin >= minimum && origin <= maximum;
        double a = (minimum - origin) / delta, b = (maximum - origin) / delta;
        low = Math.Max(low, Math.Min(a, b));
        high = Math.Min(high, Math.Max(a, b));
        return low <= high;
    }
}
