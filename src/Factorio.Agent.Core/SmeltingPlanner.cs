namespace Factorio.Agent.Core;

public sealed record SmeltingPlan(NativeRecipe Recipe, string DrillItem, ExtractionPlacement Connection, string? ExistingDrillId = null);

/// <summary>Read-only capability assessment shared by strategic selection and native execution.</summary>
public sealed class SmeltingPlanner
{
    public SmeltingPlan? Find(string item, ProductionCatalog catalog, SpatialSnapshot map,
        IReadOnlyDictionary<string, long> inventory, IReadOnlyDictionary<string, string?> knownEntityRecipes)
    {
        if (catalog.Scope != map.Scope) throw new InvalidDataException("Smelting observations span different actor scopes.");
        string[] drills = map.Items.Where(p => map.Prototypes[p.Value.EntityName].Type == "mining-drill"
            && map.Prototypes[p.Value.EntityName].FuelCategories is { Count: > 0 }).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        NativeRecipe[] recipes = catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == item
            && r.Products[0].DeterministicItem && r.Ingredients.Count == 1 && r.Ingredients[0].DeterministicItem
            && !catalog.CanHandCraft(r) && catalog.Machines.Values.Any(m => m.Categories.ContainsKey(r.Category)))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        var opportunities = new List<SmeltingPlan>();
        foreach (NativeRecipe recipe in recipes)
        {
            SpatialEntity[] receivers = map.Entities.Where(e => knownEntityRecipes.TryGetValue(e.Id, out string? currentRecipe)
                && (currentRecipe is null || currentRecipe == recipe.Name)
                && catalog.Machines.Values.Any(m => m.EntityName == e.Name && m.Categories.ContainsKey(recipe.Category))).ToArray();
            foreach (string drillItem in drills)
            {
                string name = map.Items[drillItem].EntityName;
                foreach (SpatialEntity installed in map.Entities.Where(e => e.Name == name && knownEntityRecipes.ContainsKey(e.Id)
                    && e.DropPosition is not null))
                {
                    SpatialEntity[] targets = receivers.Where(r => ExtractionPlanner.DropTile(installed.DropPosition!).Overlaps(r.Bounds)
                        && (installed.DropTargetId is null || installed.DropTargetId == r.Id)).ToArray();
                    if (targets.Length != 1) continue; // An ambiguous drop tile must not select an arbitrary receiver.
                    var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != installed.Id).ToArray() });
                    ExtractionPlacement? connection = new ExtractionPlanner().Find(field, drillItem, recipe.Ingredients[0].Name, catalog, targets)
                        .FirstOrDefault(p => p.Drill.Position == installed.Position && p.Drill.Direction == installed.Direction
                            && p.OutputPosition.DistanceTo(installed.DropPosition!) <= 0.01);
                    if (connection is not null) opportunities.Add(new(recipe, drillItem, connection, installed.Id));
                }
                if (inventory.GetValueOrDefault(drillItem) > 0)
                    opportunities.AddRange(new ExtractionPlanner().Find(new(map), drillItem, recipe.Ingredients[0].Name, catalog, receivers)
                        .Select(p => new SmeltingPlan(recipe, drillItem, p)));
            }
        }
        return opportunities.OrderByDescending(p => p.ExistingDrillId is not null).ThenBy(p => p.Connection.Drill.Score)
            .ThenBy(p => p.Recipe.Name, StringComparer.Ordinal).ThenBy(p => p.Connection.ReceiverId, StringComparer.Ordinal).FirstOrDefault();
    }
}
