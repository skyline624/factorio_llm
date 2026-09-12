namespace Factorio.Agent.Core;

/// <summary>Bounds a furnace batch by native ingredient stacks and gives slow recipes enough observations.</summary>
public static class FurnaceBatchSizing
{
    public static int SuppliedBatches(NativeRecipe recipe, long loaded, long carried)
    {
        if (loaded < 0 || carried < 0 || recipe.Ingredients.Count != 1 || !recipe.Ingredients[0].DeterministicItem)
            throw new InvalidDataException("Supplied furnace batches require a single native solid ingredient and nonnegative stocks.");
        return checked((int)Math.Floor(checked(loaded + carried) / recipe.Ingredients[0].Amount!.Value));
    }

    public static int Limit(NativeRecipe recipe, IReadOnlyDictionary<string, NativeItem> items, int requested)
    {
        if (requested < 1) throw new ArgumentOutOfRangeException(nameof(requested));
        if (recipe.Ingredients.Count == 0 || recipe.Ingredients.Any(i => !i.DeterministicItem))
            throw new InvalidDataException("Furnace batch sizing requires deterministic solid ingredients.");
        int limit = requested;
        foreach (var ingredient in recipe.Ingredients.GroupBy(i => i.Name))
        {
            if (!items.TryGetValue(ingredient.Key, out var item) || item.StackSize < 1)
                throw new InvalidDataException("Missing native ingredient stack size for furnace batch sizing.");
            double units = ingredient.Sum(i => i.Amount!.Value);
            limit = checked((int)Math.Min(limit, Math.Floor(item.StackSize / units)));
        }
        if (limit < 1) throw new InvalidOperationException("One furnace cycle exceeds a native ingredient stack.");
        return limit;
    }

    public static int ObservationLimit(NativeRecipe recipe, double craftingSpeed, int batches)
    {
        if (!double.IsFinite(craftingSpeed) || craftingSpeed <= 0 || batches < 1
            || !double.IsFinite(recipe.EnergySeconds) || recipe.EnergySeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(craftingSpeed));
        // Each ordinary wait is one game second. Extra observations cover collection and fuel maintenance;
        // native receipts and stock still determine completion, never this duration estimate.
        return checked(200 + (int)Math.Ceiling(recipe.EnergySeconds * batches / craftingSpeed));
    }
}
