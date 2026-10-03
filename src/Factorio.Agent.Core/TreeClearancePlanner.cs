namespace Factorio.Agent.Core;

/// <summary>Selects observed neutral trees for navigation or committed construction.</summary>
public sealed class TreeClearancePlanner
{
    public SpatialEntity? Select(SpatialSnapshot map, ProductionCatalog catalog, MapPosition? destination = null) =>
        map.Entities.Where(e => e.Force == "neutral" && map.Prototypes[e.Name].Type == "tree"
            && e.Position.DistanceTo(map.Actor.Position) <= Math.Min(2.5, map.Actor.ReachDistance)
            && catalog.Mining.TryGetValue(e.Name, out NativeMaterial[]? products) && products.Any(p => p.DeterministicItem))
            .OrderBy(e => e.Position.DistanceTo(destination ?? map.Actor.Position))
            .ThenBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Clears a committed footprint only when all its entity blockers are mineable neutral trees and the terrain allows it.</summary>
    public SpatialEntity? SelectPlacement(SpatialSnapshot map, ProductionCatalog catalog, string item, PlacementCandidate placement)
    {
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Construction clearance requires the current actor scope.");
        var geometry = map.Prototypes[map.Items[item].EntityName];
        var box = geometry.CollisionBox.Rotate(placement.Direction).Translate(placement.Position);
        var blockers = map.Entities.Where(e => geometry.Mask.CollidesWith(map.Prototypes[e.Name].Mask, tile: false)
            && new OrientedCollisionBox(e.Bounds, e.BoundsOrientation).TouchesOrOverlaps(box)).ToArray();
        if (blockers.Length == 0 || blockers.Any(e => e.Force != "neutral" || map.Prototypes[e.Name].Type != "tree"
            || !catalog.Mining.TryGetValue(e.Name, out NativeMaterial[]? products) || !products.Any(p => p.DeterministicItem))) return null;
        var ids = blockers.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (!new SpatialCollisionField(map with { Entities = map.Entities.Where(e => !ids.Contains(e.Id)).ToArray() })
            .PlacementClear(geometry, placement.Position, placement.Direction)) return null;
        return blockers.OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).First();
    }
}
