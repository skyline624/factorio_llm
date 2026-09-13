namespace Factorio.Agent.Core;

public sealed record SmeltingEquipment(NativeRecipe Recipe, string DrillItem, string FurnaceItem);
public sealed record SmeltingSite(SmeltingEquipment Equipment, PlacementCandidate Furnace, ExtractionPlacement Connection);

/// <summary>Plans a starter extraction site from native recipes, resource coverage and collision geometry.</summary>
public sealed class SmeltingPreparationPlanner
{
    public IReadOnlyList<SmeltingEquipment> Options(ProductionCatalog catalog, IReadOnlyDictionary<string, long> inventory, string item)
    {
        bool Obtainable(string machine) => inventory.GetValueOrDefault(machine) > 0 || catalog.Recipes.Any(r => r.Enabled
            && catalog.CanHandCraft(r) && r.Products.Count == 1 && r.Products[0].Name == machine && r.Products[0].DeterministicItem
            && r.Ingredients.All(i => i.DeterministicItem));
        return (from recipe in catalog.Recipes
                where recipe.Enabled && !catalog.CanHandCraft(recipe) && recipe.Ingredients.Count == 1 && recipe.Ingredients[0].DeterministicItem
                    && recipe.Products.Count == 1 && recipe.Products[0].Name == item && recipe.Products[0].DeterministicItem
                    && catalog.Mining.Values.Any(products => products.Length == 1 && products[0].Name == recipe.Ingredients[0].Name && products[0].DeterministicItem)
                from furnace in catalog.Machines
                where furnace.Value.Categories.ContainsKey(recipe.Category) && furnace.Value.FuelCategories.Count > 0 && Obtainable(furnace.Key)
                from drill in catalog.Items
                where drill.Value.PlaceEntityType == "mining-drill" && Obtainable(drill.Key)
                orderby furnace.Key, drill.Key
                select new SmeltingEquipment(recipe, drill.Key, furnace.Key)).ToArray();
    }

    public SmeltingSite? Find(SpatialSnapshot map, ProductionCatalog catalog, IReadOnlyDictionary<string, long> inventory, string item,
        CancellationToken token = default)
    {
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Smelting construction observations span different scopes.");
        foreach (var equipment in Options(catalog, inventory, item)
            .Where(o => map.Items.ContainsKey(o.DrillItem))
            .OrderByDescending(o => inventory.GetValueOrDefault(o.DrillItem) > 0)
            .ThenByDescending(o => inventory.GetValueOrDefault(o.FurnaceItem) > 0)
            .ThenByDescending(o => map.Prototypes[map.Items[o.DrillItem].EntityName].IsElectric))
        {
            token.ThrowIfCancellationRequested();
            if (!map.Items.TryGetValue(equipment.DrillItem, out var drillItem)
                || !map.Items.TryGetValue(equipment.FurnaceItem, out var furnaceItem)) continue;
            if (!ExtractionPlanner.SupportsSolidOutput(map.Prototypes[drillItem.EntityName])) continue;
            var site = new ExtractionPlanner().FindNewSite(map, equipment.DrillItem, equipment.FurnaceItem,
                equipment.Recipe.Ingredients[0].Name, catalog, token);
            if (site is not null) return new(equipment, site.Receiver, site.Connection);
        }
        return null;
    }
}
