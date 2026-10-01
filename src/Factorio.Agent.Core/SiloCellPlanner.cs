namespace Factorio.Agent.Core;

/// <summary>
/// Silo cells: a rocket silo fed from one chest through one inserter, in a band of its own. The silo crafts its fixed rocket-part
/// recipe (entities.lua), so it takes no recipe setting and yields no item to collect. One basic inserter moves about 48 items a
/// minute, 1.6 parts or a rocket an hour; the chains feeding it deliver far less, so one chest holds all three ingredients.
/// </summary>
public static class SiloCellPlanner
{
    public const string Kind = "silo";
    /// <summary>Silos one factory builds: a second one would draw on the same ingredient chains and only split their output.</summary>
    public const int MaximumCells = 1;

    /// <summary>Silo items cells can be built with: a native silo prototype placed by an item whose recipe is enabled.</summary>
    public static IEnumerable<string> Machines(ProductionCatalog catalog) => (catalog.Silos ?? new Dictionary<string, RocketSiloPrototype>()).Keys
        .Where(item => catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item))).Order(StringComparer.Ordinal);

    /// <summary>The silo among the machine items whose fixed recipe is this one, or null.</summary>
    public static string? Machine(ProductionCatalog catalog, NativeRecipe recipe, IReadOnlySet<string> machineItems) =>
        (catalog.Silos ?? new Dictionary<string, RocketSiloPrototype>()).Where(p => machineItems.Contains(p.Key) && p.Value.Recipe == recipe.Name)
            .Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Carried stock the actor needs so logistics can complete the silo's next batch through its cell: the rocket's remaining need,
    /// at most <see cref="RocketPlanner.BatchCycles"/> cycles as for hand supply, less what already waits in the cell's chest and
    /// inserter hand. Zero for every ingredient outside a supply step.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Procurement(RocketStep step, NativeRecipe recipe, IReadOnlyDictionary<string, long> cellStock) =>
        recipe.Ingredients.Where(i => i.DeterministicItem).GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => step.Kind != "supply" ? 0
            : checked((int)Math.Clamp(Math.Min(step.RequiredItems.GetValueOrDefault(g.Key), g.Sum(i => i.Amount!.Value) * RocketPlanner.BatchCycles)
                - cellStock.GetValueOrDefault(g.Key), 0, 1000)), StringComparer.Ordinal);
}
