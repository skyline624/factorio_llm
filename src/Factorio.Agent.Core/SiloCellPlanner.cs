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
    /// <summary>
    /// Cycles of an ingredient below which the cell calls for the actor: over a minute of its inserter's work, so a refill starts before
    /// the silo idles, without a trip for each item a swing takes.
    /// </summary>
    public const int LowWaterCycles = 2;

    /// <summary>Silo items cells can be built with: a native silo prototype placed by an item whose recipe is enabled.</summary>
    public static IEnumerable<string> Machines(ProductionCatalog catalog) => (catalog.Silos ?? new Dictionary<string, RocketSiloPrototype>()).Keys
        .Where(item => catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item))).Order(StringComparer.Ordinal);

    /// <summary>The silo among the machine items whose fixed recipe is this one, or null.</summary>
    public static string? Machine(ProductionCatalog catalog, NativeRecipe recipe, IReadOnlySet<string> machineItems) =>
        (catalog.Silos ?? new Dictionary<string, RocketSiloPrototype>()).Where(p => machineItems.Contains(p.Key) && p.Value.Recipe == recipe.Name)
            .Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Carried stock the actor brings for the silo, per ingredient: nothing while the cell's chest and inserter hand hold
    /// <see cref="LowWaterCycles"/> cycles of it, or all the rocket still needs; below that, enough to reach the cell's planned buffer of
    /// <paramref name="bufferCrafts"/> cycles, the target logistics refills the chest toward, within the remaining need. Zero for every
    /// ingredient outside a supply step.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Procurement(RocketStep step, NativeRecipe recipe, IReadOnlyDictionary<string, long> cellStock,
        int bufferCrafts) =>
        recipe.Ingredients.Where(i => i.DeterministicItem).GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            double cycle = g.Sum(i => i.Amount!.Value);
            long required = step.Kind == "supply" ? step.RequiredItems.GetValueOrDefault(g.Key) : 0;
            long stock = cellStock.GetValueOrDefault(g.Key);
            return stock >= Math.Min(required, cycle * LowWaterCycles) ? 0
                : checked((int)Math.Clamp(Math.Min(required, cycle * bufferCrafts) - stock, 0, 1000));
        }, StringComparer.Ordinal);
}
