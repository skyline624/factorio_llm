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
            if (source.SingleOrDefault(b => b.Graph is not null) is { } branched)
                return capacity * BranchAllocation(state, snapshot, branched, targetId);
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

    internal static double BranchAllocation(FactoryState state, FactorySnapshot snapshot, FactoryTransportBus bus, string targetId)
    {
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        var consumer = bus.Consumers.Single(c => c.TargetCellId == targetId);
        if (consumer.Paused) return 0;
        string pickup = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == cell.Entities[consumer.InserterRole])
            .Data.GetProperty("transport").GetProperty("pickupTargetId").GetString()!;
        string destination = bus.Graph!.Keys.Single(r => cell.Entities[r] == pickup);
        var memo = new Dictionary<string, double>(StringComparer.Ordinal);
        double Walk(string role)
        {
            if (role == destination) return 1;
            if (memo.TryGetValue(role, out double existing)) return existing;
            double value = bus.Graph[role].Outputs.Sum(Walk) * (role.StartsWith("splitter-", StringComparison.Ordinal) ? .5 : 1);
            memo[role] = value;
            return value;
        }
        // Native unbiased splitters guarantee one half per output. A blocked branch can release extra flow,
        // but that surplus is not credited before a measured delivery. No proportional paper allocation.
        return Walk(FactoryTransportHealth.Belts(cell)[0]);
    }
}
