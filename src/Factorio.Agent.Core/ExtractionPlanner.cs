namespace Factorio.Agent.Core;

public sealed record ExtractionPlacement(PlacementCandidate Drill, string ReceiverId, MapPosition OutputPosition,
    IReadOnlyList<string> ResourceIds);

/// <summary>Solves native drill output containment, tile alignment and resource coverage against observed receivers.</summary>
public sealed class ExtractionPlanner
{
    public IReadOnlyList<ExtractionPlacement> Find(SpatialCollisionField field, string drillItem, string resourceItem,
        ProductionCatalog catalog, IReadOnlyList<SpatialEntity> receivers, int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        EntityGeometry drill = field.Map.Prototypes[field.Map.Items[drillItem].EntityName];
        if (drill.Type != "mining-drill" || drill.MiningOutput is not { } vector || drill.MiningRadius is not > 0
            || !double.IsFinite(drill.MiningRadius.Value) || !double.IsFinite(vector.X) || !double.IsFinite(vector.Y)
            || drill.ResourceCategories is null) throw new InvalidDataException("Missing native mining geometry.");
        SpatialEntity[] deposits = field.Map.Entities.Where(e => e.Amount is > 0
            && field.Map.Prototypes[e.Name].ResourceCategory is { } category && drill.ResourceCategories.ContainsKey(category)).ToArray();
        var result = new List<ExtractionPlacement>();
        foreach (SpatialEntity receiver in receivers)
        {
            for (int direction = 0; direction < 16; direction += 4)
            {
                // Map positions use the engine's 1/256-tile grid; prototype vectors
                // are floating point and can otherwise miss a touching receiver.
                MapPosition rotated = Rotate(vector, direction);
                MapPosition offset = new(Math.Truncate(rotated.X * 256) / 256, Math.Truncate(rotated.Y * 256) / 256);
                int width = direction % 8 == 0 ? drill.TileWidth : drill.TileHeight;
                int height = direction % 8 == 0 ? drill.TileHeight : drill.TileWidth;
                double alignX = width % 2 * 0.5, alignY = height % 2 * 0.5;
                for (double x = Math.Ceiling(Math.Floor(receiver.Bounds.Min.X) - offset.X - alignX) + alignX; x < Math.Ceiling(receiver.Bounds.Max.X) - offset.X; x++)
                    for (double y = Math.Ceiling(Math.Floor(receiver.Bounds.Min.Y) - offset.Y - alignY) + alignY; y < Math.Ceiling(receiver.Bounds.Max.Y) - offset.Y; y++)
                    {
                        var position = new MapPosition(x, y);
                        var output = new MapPosition(x + offset.X, y + offset.Y);
                        if (!DropTile(output).Overlaps(receiver.Bounds)) continue;
                        if (!field.PlacementClear(drill, position, direction)) continue;
                        double radius = drill.MiningRadius.Value;
                        var area = new WorldBox(new(x - radius, y - radius), new(x + radius, y + radius));
                        if (!field.Map.Bounds.Contains(area)) continue;
                        SpatialEntity[] covered = deposits.Where(e => area.Overlaps(e.Bounds)).ToArray();
                        if (!covered.Any(e => area.Contains(e.Position)) || covered.Any(e => !catalog.Mining.TryGetValue(e.Name, out var products)
                            || products.Length == 0 || products.Any(p => !p.DeterministicItem || p.Name != resourceItem))) continue;
                        result.Add(new(new(position, direction, position.DistanceTo(field.Map.Actor.Position)), receiver.Id,
                            output, covered.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray()));
                    }
            }
        }
        return result.OrderBy(p => p.Drill.Score).ThenBy(p => p.ReceiverId, StringComparer.Ordinal)
            .ThenBy(p => p.Drill.Direction).Take(limit).ToArray();
    }

    // Native drop_target uses intersection with the tile under the drop position,
    // not containment of the point in the receiving entity's collision box.
    public static WorldBox DropTile(MapPosition point) => new(new(Math.Floor(point.X), Math.Floor(point.Y)),
        new(Math.Floor(point.X) + 1, Math.Floor(point.Y) + 1));

    public static MapPosition Rotate(MapPosition vector, int direction) => direction switch
    {
        0 => vector,
        4 => new(-vector.Y, vector.X),
        8 => new(-vector.X, -vector.Y),
        12 => new(vector.Y, -vector.X),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}
