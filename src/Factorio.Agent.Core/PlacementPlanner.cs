namespace Factorio.Agent.Core;

public sealed record PlacementCandidate(MapPosition Position, int Direction, double Score);

/// <summary>Enumerates native tile-aligned placements from geometry; no predefined layout coordinates.</summary>
public sealed class PlacementPlanner
{
    public IReadOnlyList<PlacementCandidate> FindCandidates(SpatialCollisionField field, string item,
        MapPosition preferredPosition, bool requireBuildReach = true, int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!field.Map.Items.TryGetValue(item, out PlaceableItem? placeable))
            throw new ArgumentException("Request native geometry for the construction item before planning.", nameof(item));
        EntityGeometry geometry = field.Map.Prototypes[placeable.EntityName];
        var candidates = new List<PlacementCandidate>();
        for (int direction = 0; direction < 16; direction += 4)
        {
            int width = direction % 8 == 0 ? geometry.TileWidth : geometry.TileHeight;
            int height = direction % 8 == 0 ? geometry.TileHeight : geometry.TileWidth;
            for (double x = Math.Ceiling(field.Map.Bounds.Min.X) + width % 2 * 0.5; x < field.Map.Bounds.Max.X; x++)
                for (double y = Math.Ceiling(field.Map.Bounds.Min.Y) + height % 2 * 0.5; y < field.Map.Bounds.Max.Y; y++)
                {
                    var position = new MapPosition(x, y);
                    if (requireBuildReach && position.DistanceTo(field.Map.Actor.Position) > field.Map.Actor.BuildDistance) continue;
                    if (!field.PlacementClear(geometry, position, direction)) continue;
                    candidates.Add(new(position, direction, position.DistanceTo(preferredPosition)));
                }
        }
        return candidates.OrderBy(c => c.Score).ThenBy(c => c.Position.Y).ThenBy(c => c.Position.X)
            .ThenBy(c => c.Direction).Take(limit).ToArray();
    }
}
