namespace Factorio.Agent.Core;

public sealed record SmeltingFuelPlan(string Fuel, int DrillReserve, int FurnaceReserve,
    double DrillWorkJoules, double FurnaceWorkJoules, double ReserveMargin)
{
    public int ProcurementTarget(bool refuellingDrill, long carried, long drillStock, long furnaceStock, bool includeDrillReserve = true)
    {
        long missingDrill = includeDrillReserve ? Math.Max(0, DrillReserve - drillStock) : 0;
        long missingFurnace = Math.Max(0, FurnaceReserve - furnaceStock);
        long neededNow = refuellingDrill ? missingDrill : missingFurnace;
        return carried < neededNow ? checked((int)(missingDrill + missingFurnace)) : 0;
    }
}

/// <summary>Prepares a bounded reserve for both machines from native rates; output remains proven by engine observations.</summary>
public sealed class SmeltingFuelPlanner
{
    public SmeltingFuelPlan Choose(SmeltingPlan plan, SpatialSnapshot map, ProductionCatalog catalog, int missingOutput,
        IReadOnlyDictionary<string, long> carried, IReadOnlyDictionary<string, long> available)
    {
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Smelting fuel observations span different scopes.");
        if (missingOutput < 1) throw new ArgumentOutOfRangeException(nameof(missingOutput));
        var drill = map.Prototypes[map.Items[plan.DrillItem].EntityName];
        var furnace = map.Prototypes[map.Entities.Single(e => e.Id == plan.Connection.ReceiverId).Name];
        var nativeFurnace = catalog.Machines.Values.First(m => m.EntityName == furnace.Name);
        double batches = Math.Ceiling(missingOutput / plan.Recipe.Products[0].Amount!.Value);
        double drillEnergy = ExtractionPlanner.WorkEnergy(plan.Connection, map, plan.DrillItem, catalog,
            plan.Recipe.Ingredients[0].Name, batches * plan.Recipe.Ingredients[0].Amount!.Value);
        double furnaceEnergy = batches * plan.Recipe.EnergySeconds / Positive(nativeFurnace.CraftingSpeed) * 60
            * Positive(furnace.EnergyPerTick) / Positive(furnace.BurnerEffectivity);
        if (!double.IsFinite(drillEnergy + furnaceEnergy)) throw new InvalidDataException("Invalid native smelting fuel estimate.");
        const double margin = 1.25;
        return catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.StackSize > 0 && p.Value.FuelCategory is { } category
                && drill.FuelCategories?.ContainsKey(category) == true && nativeFurnace.FuelCategories.ContainsKey(category)
                && catalog.Mining.Values.Any(products => products.Any(m => m.Name == p.Key && m.DeterministicItem)))
            .Select(p => new SmeltingFuelPlan(p.Key, Reserve(drillEnergy, p.Value), Reserve(furnaceEnergy, p.Value), drillEnergy, furnaceEnergy, margin))
            .OrderByDescending(p => available.GetValueOrDefault(p.Fuel) >= p.DrillReserve + p.FurnaceReserve)
            .ThenByDescending(p => available.GetValueOrDefault(p.Fuel) > 0)
            .ThenByDescending(p => carried.GetValueOrDefault(p.Fuel) > 0)
            .ThenByDescending(p => catalog.Items[p.Fuel].FuelValue).ThenBy(p => p.Fuel, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("No common obtainable fuel for the drill and furnace.");

        static int Reserve(double energy, NativeItem item) => checked((int)Math.Clamp(Math.Ceiling(energy * margin / item.FuelValue), 1, item.StackSize));
    }

    private static double Positive(double? value) => value is { } n && double.IsFinite(n) && n > 0 ? n
        : throw new InvalidDataException("Missing or invalid native mining, energy or burner efficiency evidence.");
}
