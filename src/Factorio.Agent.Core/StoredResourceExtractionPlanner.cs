namespace Factorio.Agent.Core;

public sealed record ResourceExtractionEquipment(string DrillItem, string ChestItem);
public sealed record ResourceExtractionPlan(ResourceExtractionEquipment Equipment, ExtractionPlacement Connection,
    PlacementCandidate? NewChest = null, string? ExistingDrillId = null);

/// <summary>Plans native resource extraction into storage, preserving observed existing installations first.</summary>
public sealed class StoredResourceExtractionPlanner
{
    public IReadOnlyList<ResourceExtractionEquipment> Options(ProductionCatalog catalog, IReadOnlyDictionary<string, long> inventory, string item)
    {
        if (catalog.MiningSourceTypes is null || !catalog.Mining.Any(p => catalog.MiningSourceTypes.GetValueOrDefault(p.Key) == "resource"
                && p.Value.Length == 1 && p.Value[0].Name == item && p.Value[0].DeterministicItem)) return [];
        double Cost(string equipment) => inventory.GetValueOrDefault(equipment) > 0 ? 0 : catalog.Recipes
            .Where(r => r.Enabled && catalog.CanHandCraft(r) && r.Products.Count == 1 && r.Products[0].Name == equipment
                && r.Products[0].DeterministicItem && r.Ingredients.All(i => i.DeterministicItem))
            .Select(r => r.Ingredients.Sum(i => i.Amount!.Value) / r.Products[0].Amount!.Value).DefaultIfEmpty(double.PositiveInfinity).Min();
        return (from drill in catalog.Items where drill.Value.PlaceEntityType == "mining-drill" && double.IsFinite(Cost(drill.Key))
                from chest in catalog.Items where chest.Value.PlaceEntityType == "container" && double.IsFinite(Cost(chest.Key))
                orderby Cost(drill.Key) + Cost(chest.Key), drill.Key, chest.Key
                select new ResourceExtractionEquipment(drill.Key, chest.Key)).ToArray();
    }

    public ResourceExtractionPlan? Find(string item, ProductionCatalog catalog, SpatialSnapshot map,
        IReadOnlyDictionary<string, long> inventory, IReadOnlyDictionary<string, KnownProductionMachine> owned,
        bool allowConstruction = true, CancellationToken token = default)
    {
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Resource extraction observations span different scopes.");
        var options = Options(catalog, inventory, item).Where(o => map.Items.TryGetValue(o.DrillItem, out var drill)
            && (map.Prototypes[drill.EntityName].FuelCategories is { Count: > 0 }
                || map.Prototypes[drill.EntityName] is { IsElectric: true, MiningOutput: not null })
            && map.Items.ContainsKey(o.ChestItem))
            .OrderByDescending(o => map.Prototypes[map.Items[o.DrillItem].EntityName].IsElectric)
            .ThenByDescending(o => map.Prototypes[map.Items[o.DrillItem].EntityName].MiningSpeed).ToArray();
        var receivers = map.Entities.Where(e => map.Prototypes[e.Name].Type == "container" && owned.TryGetValue(e.Id, out var known)
            && known.Output is not null && known.Output.All(p => p.Value == 0 || p.Key == item)).ToArray();
        var planner = new ExtractionPlanner();
        var candidates = new List<ResourceExtractionPlan>();
        foreach (var equipment in options)
        {
            token.ThrowIfCancellationRequested();
            candidates.AddRange(planner.FindInstalled(map, equipment.DrillItem, item, catalog, receivers, owned.Keys.ToHashSet(StringComparer.Ordinal))
                .Select(p => new ResourceExtractionPlan(equipment, p.Connection, ExistingDrillId: p.DrillId)));
            if (allowConstruction) candidates.AddRange(planner.Find(new(map), equipment.DrillItem, item, catalog, receivers)
                .Select(p => new ResourceExtractionPlan(equipment, p)));
        }
        if (candidates.OrderByDescending(p => p.ExistingDrillId is not null)
                .ThenByDescending(p => map.Prototypes[map.Items[p.Equipment.DrillItem].EntityName].IsElectric)
                .ThenBy(p => p.Connection.Drill.Score)
                .ThenBy(p => p.Connection.ReceiverId, StringComparer.Ordinal).FirstOrDefault() is { } existing) return existing;
        if (!allowConstruction) return null;
        foreach (var equipment in options)
        {
            token.ThrowIfCancellationRequested();
            var site = planner.FindNewSite(map, equipment.DrillItem, equipment.ChestItem, item, catalog, token);
            if (site is not null) return new(equipment, site.Connection, site.Receiver);
        }
        return null;
    }
}
