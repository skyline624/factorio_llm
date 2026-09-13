namespace Factorio.Agent.Core;

public sealed record FurnaceFuelPlan(string Fuel, int Reserve, double WorkJoules);

/// <summary>Values a bounded burner reserve from native work; new ordinary fuel comes from solid deposits.</summary>
public static class FurnaceFuelPlanner
{
    public static FurnaceFuelPlan Choose(NativeRecipe recipe, NativeFurnace furnace, EntityGeometry geometry, int batches,
        ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, IReadOnlyDictionary<string, long> available,
        bool allowManualBootstrap) => ChooseFleet(recipe, furnace, geometry, [batches], catalog, carried, available, allowManualBootstrap);

    public static FurnaceFuelPlan ChooseFleet(NativeRecipe recipe, NativeFurnace furnace, EntityGeometry geometry, IReadOnlyList<int> batches,
        ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, IReadOnlyDictionary<string, long> available,
        bool allowManualBootstrap)
    {
        if (batches.Count is < 1 or > FurnaceFleetPlanner.MaximumMachines || batches.Any(n => n < 1)
            || geometry.Name != furnace.EntityName || !furnace.Categories.ContainsKey(recipe.Category)
            || carried.Values.Any(n => n < 0) || available.Values.Any(n => n < 0))
            throw new InvalidDataException("Inconsistent native furnace fuel inputs.");
        double[] workByMachine = batches.Select(n => n * Positive(recipe.EnergySeconds) / Positive(furnace.CraftingSpeed) * 60
            * Positive(geometry.EnergyPerTick) / Positive(geometry.BurnerEffectivity)).ToArray();
        double work = workByMachine.Sum();
        if (!double.IsFinite(work)) throw new InvalidDataException("Invalid native furnace energy estimate.");
        bool Obtainable(string name) => SolidFuelSources.CanExtract(catalog, name, allowManualBootstrap);
        return catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.StackSize > 0 && p.Value.FuelCategory is { } category
                && furnace.FuelCategories.ContainsKey(category)
                && (carried.GetValueOrDefault(p.Key) > 0 || available.GetValueOrDefault(p.Key) > 0
                    || Obtainable(p.Key)))
            .Select(p =>
            {
                int reserve = checked((int)Math.Min(1000, workByMachine.Sum(w =>
                    Math.Clamp(Math.Ceiling(w * 1.25 / p.Value.FuelValue), 1, Math.Min(1000, p.Value.StackSize)))));
                // Existing wood or manufactured fuel may be used, but does not authorize harvesting or fabricating a fresh reserve.
                if (!Obtainable(p.Key)) reserve = checked((int)Math.Min(reserve,
                    Math.Max(carried.GetValueOrDefault(p.Key), available.GetValueOrDefault(p.Key))));
                return new FurnaceFuelPlan(p.Key, reserve, work);
            })
            .OrderByDescending(p => carried.GetValueOrDefault(p.Fuel) >= p.Reserve)
            .ThenByDescending(p => available.GetValueOrDefault(p.Fuel) >= p.Reserve)
            .ThenByDescending(p => carried.GetValueOrDefault(p.Fuel) > 0)
            .ThenByDescending(p => available.GetValueOrDefault(p.Fuel) > 0)
            .ThenByDescending(p => catalog.Items[p.Fuel].FuelValue).ThenBy(p => p.Fuel, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("No compatible stored or obtainable solid-deposit fuel for the furnace.");
    }

    private static double Positive(double? value) => value is { } number && double.IsFinite(number) && number > 0 ? number
        : throw new InvalidDataException("Missing or invalid native furnace work, energy or burner efficiency.");
}
