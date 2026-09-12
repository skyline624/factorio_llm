namespace Factorio.Agent.Core;

public sealed record PlacementCandidate(MapPosition Position, int Direction, double Score);

/// <summary>Enumerates native tile-aligned placements from geometry; no predefined layout coordinates.</summary>
public sealed class PlacementPlanner
{
    public MapPosition? FindApproach(SpatialCollisionField field, string item, PlacementCandidate placement)
    {
        EntityGeometry building = field.Map.Prototypes[field.Map.Items[item].EntityName];
        WorldBox footprint = building.CollisionBox.Rotate(placement.Direction).Translate(placement.Position);
        var exclusion = new WorldBox(new(footprint.Min.X - 0.4, footprint.Min.Y - 0.4), new(footprint.Max.X + 0.4, footprint.Max.Y + 0.4));
        var candidates = new List<MapPosition>();
        double reach = field.Map.Actor.BuildDistance - 1;
        for (double x = Math.Ceiling((placement.Position.X - reach) * 2) / 2; x <= placement.Position.X + reach; x += 0.5)
            for (double y = Math.Ceiling((placement.Position.Y - reach) * 2) / 2; y <= placement.Position.Y + reach; y += 0.5)
            {
                var point = new MapPosition(x, y);
                if (point.DistanceTo(placement.Position) > reach || exclusion.Overlaps(field.Character.CollisionBox.Translate(point))
                    || !field.Walkable(point)) continue;
                candidates.Add(point);
            }
        return candidates.OrderBy(p => p.DistanceTo(field.Map.Actor.Position)).ThenBy(p => p.DistanceTo(placement.Position))
            .FirstOrDefault(p => new RoutePlanner().Find(field, p).Status == RouteStatus.Found);
    }

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
