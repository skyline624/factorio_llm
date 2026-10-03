using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Healthy native links whose nominal supply covers the consumer's registered recipe share.</summary>
internal static class FactoryTransportCoverage
{
    public static HashSet<(string Chest, string Item)> Connected(FactoryState state, FactorySnapshot snapshot,
        ProductionCatalog catalog, IReadOnlyDictionary<string, double>? shares)
    {
        var buses = (state.Transports ?? []).Where(b => FactoryTransportHealth.Healthy(state, snapshot, b)).ToArray();
        var connected = new HashSet<(string, string)>();
        foreach (var flow in buses.SelectMany(b => b.Consumers.Select(c => (c.TargetCellId, b.Item))).Distinct())
        {
            var target = state.Cells.Single(c => c.Id == flow.TargetCellId);
            double? demand = Demand(target, flow.Item);
            if (demand is null || Supply(target.Id, flow.Item) + 1e-9 >= demand)
                connected.Add((target.Entities["input-chest"], flow.Item));
        }
        return connected;

        double? Demand(FactoryCell cell, string item)
        {
            if (cell.Recipe is null || shares is null || !shares.TryGetValue(cell.Recipe, out double crafts)) return null;
            var recipe = catalog.Recipes.SingleOrDefault(r => r.Name == cell.Recipe);
            double amount = recipe?.Ingredients.Where(i => i.DeterministicItem && i.Name == item).Sum(i => i.Amount!.Value) ?? 0;
            return amount > 0 ? amount * crafts : null;
        }

        double Supply(string targetId, string item) => buses.Where(b => b.Item == item
            && b.Consumers.Any(c => c.TargetCellId == targetId && !c.Paused)).GroupBy(b => b.SourceCellId, StringComparer.Ordinal).Sum(source =>
        {
            var cell = state.Cells.Single(c => c.Id == source.Key);
            double capacity = Capacity(cell, item);
            var consumers = buses.Where(b => b.SourceCellId == cell.Id && b.Item == item)
                .SelectMany(b => b.Consumers.Where(c => !c.Paused)).Select(c => c.TargetCellId).Distinct(StringComparer.Ordinal)
                .Select(id => state.Cells.Single(c => c.Id == id)).ToArray();
            // An unplanned consumer still uses the common source arm. Reserve an equal share when its demand is unknown.
            if (consumers.Any(c => Demand(c, item) is null)) return capacity / consumers.Length;
            double totalDemand = consumers.Sum(c => Demand(c, item)!.Value);
            return capacity * Demand(state.Cells.Single(c => c.Id == targetId), item)!.Value / Math.Max(capacity, totalDemand);
        });

        double Capacity(FactoryCell source, string item)
        {
            double perMinute;
            if (source.IsResource)
                perMinute = (state.Rows ?? []).SingleOrDefault(r => r.Id == source.Slot.Band && r.Product == item)?.CellPerMinute ?? 0;
            else
            {
                if (source.Recipe is null || shares is null || !shares.TryGetValue(source.Recipe, out double crafts)) return 0;
                var recipe = catalog.Recipes.SingleOrDefault(r => r.Name == source.Recipe);
                if (recipe is null) return 0;
                perMinute = Math.Min(crafts, AutomationPlanner.CellCraftsPerMinute(catalog, recipe, source.MachineItem))
                    * recipe.Products.Where(p => p.DeterministicItem && p.Name == item).Sum(p => p.Amount!.Value);
            }
            return Math.Min(perMinute, 60 * AutomationPlanner.InserterItemsPerSecond);
        }
    }
}
