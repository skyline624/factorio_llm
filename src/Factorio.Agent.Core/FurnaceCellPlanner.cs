namespace Factorio.Agent.Core;

/// <summary>
/// Furnace band cells: a burner furnace fed from one chest that holds both its ingredient and its fuel, so the input
/// inserter loads the fuel slot natively and the furnace picks its recipe from what it receives. Bands smelt inputs
/// that are made rather than mined, such as steel from plates; ore smelting stays on the patch with resource cells.
/// </summary>
public static class FurnaceCellPlanner
{
    public const string Kind = "furnace";

    /// <summary>The fastest burner furnace whose recipe is enabled and whose burner accepts the fuel, or null.</summary>
    public static string? Machine(ProductionCatalog catalog, string fuel) =>
        catalog.Items.TryGetValue(fuel, out var item) && item.FuelCategory is { } category
            ? catalog.Machines.Where(p => p.Value.FuelCategories.ContainsKey(category) && Craftable(catalog, p.Key))
                .OrderByDescending(p => p.Value.CraftingSpeed).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault()
            : null;

    /// <summary>
    /// Whether the furnace can serve the recipe from its input chest: a craftable burner furnace, an enabled solid recipe
    /// of one ingredient and one product (the source slot selects it) in a category it smelts, and a fuel it burns.
    /// Null when it can.
    /// </summary>
    public static string? Failure(ProductionCatalog catalog, string machineItem, string? recipe, string fuel)
    {
        if (!catalog.Machines.TryGetValue(machineItem, out var furnace)) return $"{machineItem} is not a native burner furnace.";
        if (!Craftable(catalog, machineItem)) return $"No enabled recipe makes {machineItem}.";
        if (recipe is null || catalog.Recipes.FirstOrDefault(r => r.Name == recipe) is not { } native || !Smeltable(native, furnace))
            return $"{machineItem} cannot smelt {recipe ?? "an unnamed recipe"} from one enabled solid ingredient.";
        return catalog.Items.TryGetValue(fuel, out var item) && item.FuelValue > 0 && item.FuelCategory is { } category
            && furnace.FuelCategories.ContainsKey(category) ? null : $"{machineItem} does not burn {fuel}.";
    }

    /// <summary>
    /// Fuel the input chest keeps for the buffered crafts: native recipe work at the furnace's crafting speed and burner
    /// usage over the fuel value, and never below a quarter stack so the furnace keeps running between actor visits.
    /// </summary>
    public static int FuelReserve(NativeRecipe recipe, NativeFurnace furnace, EntityGeometry geometry, NativeItem fuel, int crafts)
    {
        if (crafts is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(crafts));
        if (geometry.Name != furnace.EntityName || !furnace.Categories.ContainsKey(recipe.Category))
            throw new InvalidDataException("The furnace geometry or category does not match the recipe.");
        double joules = crafts * recipe.EnergySeconds / Positive(furnace.CraftingSpeed) * 60 * Positive(geometry.EnergyPerTick)
            / Positive(geometry.BurnerEffectivity);
        double units = Math.Ceiling(joules / Positive(fuel.FuelValue) - 1e-9);
        if (!double.IsFinite(units) || units > 100000) throw new InvalidDataException("Invalid native furnace fuel estimate.");
        return Math.Max((int)units, fuel.StackSize / 4);
    }

    /// <summary>Furnaces select their recipe from one source slot, so only single-ingredient solid smelting runs there.</summary>
    internal static bool Smeltable(NativeRecipe recipe, NativeFurnace furnace) => recipe.Enabled && furnace.Categories.ContainsKey(recipe.Category)
        && recipe.Ingredients.Count == 1 && recipe.Ingredients[0].DeterministicItem
        && recipe.Products.Count == 1 && recipe.Products[0].DeterministicItem;

    private static bool Craftable(ProductionCatalog catalog, string item) => catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));

    private static double Positive(double? value) => value is { } number && double.IsFinite(number) && number > 0 ? number
        : throw new InvalidDataException("Missing or invalid native furnace speed, energy, burner effectivity or fuel value.");
}
