namespace Factorio.Agent.Core;

public sealed record SmeltingPlan(NativeRecipe Recipe, string DrillItem, ExtractionPlacement Connection, string? ExistingDrillId = null);

/// <summary>Read-only capability assessment shared by strategic selection and native execution.</summary>
public sealed class SmeltingPlanner
{
    public SmeltingPlan? Find(string item, ProductionCatalog catalog, SpatialSnapshot map,
        IReadOnlyDictionary<string, long> inventory, IReadOnlyDictionary<string, KnownProductionMachine> knownMachines,
        IReadOnlySet<string>? constructibleDrills = null)
    {
        if (catalog.Scope != map.Scope) throw new InvalidDataException("Smelting observations span different actor scopes.");
        string[] drills = map.Items.Where(p => ExtractionPlanner.SupportsSolidOutput(map.Prototypes[p.Value.EntityName]))
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        NativeRecipe[] recipes = catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == item
            && r.Products[0].DeterministicItem && r.Ingredients.Count == 1 && r.Ingredients[0].DeterministicItem
            && !catalog.CanHandCraft(r) && catalog.Machines.Values.Any(m => m.Categories.ContainsKey(r.Category)))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        var opportunities = new List<SmeltingPlan>();
        foreach (NativeRecipe recipe in recipes)
        {
            SpatialEntity[] receivers = map.Entities.Where(e => knownMachines.TryGetValue(e.Id, out KnownProductionMachine? machine)
                && machine.CanProcess(recipe)
                && catalog.Machines.Values.Any(m => m.EntityName == e.Name && m.Categories.ContainsKey(recipe.Category))).ToArray();
            foreach (string drillItem in drills)
            {
                foreach (var installed in new ExtractionPlanner().FindInstalled(map, drillItem, recipe.Ingredients[0].Name, catalog, receivers, knownMachines.Keys.ToHashSet(StringComparer.Ordinal)))
                    opportunities.Add(new(recipe, drillItem, installed.Connection, installed.DrillId));
                if (inventory.GetValueOrDefault(drillItem) > 0 || constructibleDrills?.Contains(drillItem) == true)
                    opportunities.AddRange(new ExtractionPlanner().Find(new(map), drillItem, recipe.Ingredients[0].Name, catalog, receivers)
                        .Select(p => new SmeltingPlan(recipe, drillItem, p)));
            }
        }
        return opportunities.OrderByDescending(p => p.ExistingDrillId is not null)
            .ThenByDescending(p => inventory.GetValueOrDefault(p.DrillItem) > 0)
            .ThenByDescending(p => map.Prototypes[map.Items[p.DrillItem].EntityName].IsElectric)
            .ThenBy(p => p.Connection.Drill.Score)
            .ThenBy(p => p.Recipe.Name, StringComparer.Ordinal).ThenBy(p => p.Connection.ReceiverId, StringComparer.Ordinal).FirstOrDefault();
    }
}
