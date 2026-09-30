namespace Factorio.Agent.Core;

public sealed record FactoryZoneSite(MapPosition Origin, int Slots, IReadOnlyList<SpatialEntity> Clearance);

/// <summary>
/// Chooses where a factory band may grow on observed terrain. Water, deposits and existing buildings exclude an area;
/// trees and rocks are reported for mining because the actor can legitimately remove them.
/// </summary>
public sealed class FactoryZonePlanner
{
    public static readonly HashSet<string> Removable = new(StringComparer.Ordinal) { "tree", "simple-entity" };
    private static readonly CollisionMask Floor = new(["item", "object", "water_tile"], false, false, false);

    public FactoryZoneSite? Find(SpatialSnapshot map, EntityGeometry machine, MapPosition preferred, int slots,
        CancellationToken token = default)
    {
        if (slots is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(slots));
        // A one-tile margin keeps band ends open to the walkway and away from unrelated machines.
        int width = slots * FactoryBandPlanner.Pitch(machine) + 2, height = FactoryBandPlanner.BandHeight(machine) + 2;
        var grid = new TileGrid(map);
        var candidates = new List<(int X, int Y, double Score)>();
        for (int x = grid.MinX; x + width <= grid.MinX + grid.Width; x++)
            for (int y = grid.MinY; y + height <= grid.MinY + grid.Height; y++)
                if (grid.Free(x, y, width, height))
                    candidates.Add((x, y, new MapPosition(x + width / 2.0, y + height / 2.0).DistanceTo(preferred)));
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return null;
        var (bestX, bestY, _) = candidates.OrderBy(c => c.Score).ThenBy(c => c.Y).ThenBy(c => c.X).First();
        var area = new WorldBox(new(bestX, bestY), new(bestX + width, bestY + height));
        return new(new(bestX + 1, bestY + 1), slots, grid.Removables(area));
    }

    /// <summary>Removable obstacles inside an area, or null if water, deposits, buildings or unknown terrain block it.</summary>
    public static IReadOnlyList<SpatialEntity>? Clearance(SpatialSnapshot map, WorldBox area)
    {
        var grid = new TileGrid(map);
        int x = (int)Math.Floor(area.Min.X), y = (int)Math.Floor(area.Min.Y);
        int w = (int)Math.Ceiling(area.Max.X) - x, h = (int)Math.Ceiling(area.Max.Y) - y;
        return grid.Contains(x, y, w, h) && grid.Free(x, y, w, h) ? grid.Removables(area) : null;
    }

    private sealed class TileGrid
    {
        private readonly int[,] blocked; // prefix sums of blocked tiles
        private readonly SpatialSnapshot map;
        public int MinX { get; }
        public int MinY { get; }
        public int Width { get; }
        public int Height { get; }

        public TileGrid(SpatialSnapshot map)
        {
            this.map = map;
            MinX = (int)Math.Ceiling(map.Bounds.Min.X);
            MinY = (int)Math.Ceiling(map.Bounds.Min.Y);
            Width = (int)Math.Floor(map.Bounds.Max.X) - MinX;
            Height = (int)Math.Floor(map.Bounds.Max.Y) - MinY;
            var cell = new bool[Width, Height];
            foreach (TileRun row in map.Rows)
                if (map.TilePrototypes.TryGetValue(row.Name, out var mask) && Floor.CollidesWith(mask, tile: true))
                    for (int x = row.X; x < row.X + row.Length; x++) Mark(cell, x, row.Y);
            foreach (SpatialEntity entity in map.Entities)
            {
                EntityGeometry prototype = map.Prototypes[entity.Name];
                if (entity.Id == map.Actor.Id || Removable.Contains(prototype.Type)
                    || prototype.Type != "resource" && prototype.Mask.Layers.Count == 0) continue;
                // Deposits count as blocked even without a collision layer: building over ore wastes future mining.
                for (int x = (int)Math.Floor(entity.Bounds.Min.X); x < Math.Ceiling(entity.Bounds.Max.X); x++)
                    for (int y = (int)Math.Floor(entity.Bounds.Min.Y); y < Math.Ceiling(entity.Bounds.Max.Y); y++)
                        Mark(cell, x, y);
            }
            blocked = new int[Width + 1, Height + 1];
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    blocked[x + 1, y + 1] = (cell[x, y] ? 1 : 0) + blocked[x, y + 1] + blocked[x + 1, y] - blocked[x, y];
        }

        private void Mark(bool[,] cell, int x, int y)
        {
            if (x >= MinX && y >= MinY && x < MinX + Width && y < MinY + Height) cell[x - MinX, y - MinY] = true;
        }

        public bool Contains(int x, int y, int w, int h) => x >= MinX && y >= MinY && x + w <= MinX + Width && y + h <= MinY + Height;

        public bool Free(int x, int y, int w, int h)
        {
            int x0 = x - MinX, y0 = y - MinY;
            return blocked[x0 + w, y0 + h] - blocked[x0, y0 + h] - blocked[x0 + w, y0] + blocked[x0, y0] == 0;
        }

        public IReadOnlyList<SpatialEntity> Removables(WorldBox area) => map.Entities
            .Where(e => e.Id != map.Actor.Id && Removable.Contains(map.Prototypes[e.Name].Type) && e.Bounds.Overlaps(area))
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
}
