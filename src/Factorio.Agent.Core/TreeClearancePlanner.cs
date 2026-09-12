namespace Factorio.Agent.Core;

/// <summary>Selects only observed neutral trees within conservative native mining reach.</summary>
public sealed class TreeClearancePlanner
{
    public SpatialEntity? Select(SpatialSnapshot map, ProductionCatalog catalog, MapPosition? destination = null) =>
        map.Entities.Where(e => e.Force == "neutral" && map.Prototypes[e.Name].Type == "tree"
            && e.Position.DistanceTo(map.Actor.Position) <= Math.Min(2.5, map.Actor.ReachDistance)
            && catalog.Mining.TryGetValue(e.Name, out NativeMaterial[]? products) && products.Any(p => p.DeterministicItem))
            .OrderBy(e => e.Position.DistanceTo(destination ?? map.Actor.Position))
            .ThenBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
}
