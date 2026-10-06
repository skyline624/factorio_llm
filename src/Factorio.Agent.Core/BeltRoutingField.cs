namespace Factorio.Agent.Core;

/// <summary>Immutable native collision and surface-flow reservations for one routing attempt.</summary>
internal sealed class BeltRoutingField
{
    private readonly SpatialCollisionField field;
    private readonly EntityGeometry geometry;
    private readonly HashSet<MapPosition> flowBlocked = [];
    private readonly Dictionary<MapPosition, bool> clearance = [];

    public BeltRoutingField(SpatialSnapshot map, EntityGeometry geometry, MapPosition start,
        CancellationToken token, string? inletBeltId = null, IReadOnlySet<string>? existingBusBelts = null,
        SpatialCollisionField? collisionField = null)
    {
        this.geometry = geometry;
        field = collisionField ?? new(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var entity in map.Entities)
        {
            token.ThrowIfCancellationRequested();
            if (map.Prototypes[entity.Name].Type is "transport-belt" or "underground-belt" or "splitter")
            {
                var box = new WorldBox(new(entity.Bounds.Min.X - 1, entity.Bounds.Min.Y - 1),
                    new(entity.Bounds.Max.X + 1, entity.Bounds.Max.Y + 1));
                bool retained = existingBusBelts?.Contains(entity.Id) == true && map.Prototypes[entity.Name].Type == "transport-belt";
                for (double x = Math.Ceiling(Math.Max(box.Min.X, map.Bounds.Min.X) - .5) + .5;
                    x <= Math.Min(box.Max.X, map.Bounds.Max.X); x++)
                for (double y = Math.Ceiling(Math.Max(box.Min.Y, map.Bounds.Min.Y) - .5) + .5;
                    y <= Math.Min(box.Max.Y, map.Bounds.Max.Y); y++)
                {
                    var position = new MapPosition(x, y);
                    if (!(position == start && entity.Id == inletBeltId)
                        && !(retained && Front(entity.Position, entity.Direction) != position)) flowBlocked.Add(position);
                }
            }
            if (entity.PickupPosition is { } pickup) flowBlocked.Add(BeltRoutePlanner.Cell(pickup));
            if (entity.DropPosition is { } drop) flowBlocked.Add(BeltRoutePlanner.Cell(drop));
        }
    }

    public bool SurfaceClear(MapPosition position)
    {
        if (BeltRoutePlanner.Cell(position) != position) return false;
        if (clearance.TryGetValue(position, out bool known)) return known;
        return clearance[position] = TransportConstructionSafety.Allows(field.Map, position)
            && field.PlacementClear(geometry, position, 0) && !flowBlocked.Contains(position);
    }

    internal static MapPosition Front(MapPosition position, int direction, int distance = 1)
    {
        var offset = ExtractionPlanner.Rotate(new(0, -distance), direction);
        return new(position.X + offset.X, position.Y + offset.Y);
    }
}
