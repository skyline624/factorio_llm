namespace Factorio.Agent.Core;

public sealed record FluidSupplyRoute(string SourceId, PipeRoutePlan Route);

public sealed class FluidSupplyPlanner
{
    public FluidSupplyRoute? Find(SpatialSnapshot map, FactorySnapshot stock, string pipeItem, string targetId, string fluid)
    {
        if (map.Scope != stock.Scope) throw new InvalidDataException("Fluid supply observations have different actor scopes.");
        var target = map.Entities.Single(e => e.Id == targetId);
        foreach (var source in map.Entities.Where(e => e.Id != targetId && e.Force == target.Force
            && stock.FluidStockAt(e.Id, fluid) > 0).OrderBy(e => e.Position.DistanceTo(target.Position)).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            var route = new PipeRoutePlanner().Find(map, pipeItem, source.Id, targetId, fluid);
            if (route.Status == PipeRouteStatus.Found && route.Source is { } output
                && stock.FluidStockAt(source.Id, fluid, output.BoxIndex) > 0)
                return new(source.Id, route);
        }
        return null;
    }
}
