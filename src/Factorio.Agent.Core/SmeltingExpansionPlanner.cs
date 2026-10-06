namespace Factorio.Agent.Core;

/// <summary>
/// How many direct drill-to-furnace pairs one long smelting wait is worth. A burner pair delivers about 15 plates a minute;
/// on 2026-10-06 (seed 20261107) one pair spent 17 minutes on 236 iron plates while the actor only waited beside it.
/// </summary>
public static class SmeltingExpansionPlanner
{
    public const int MaximumPairs = 4;

    /// <summary>Game seconds a wait may last before another pair, built from the waiting actor's own stock, pays for itself.</summary>
    public const double TargetGameSeconds = 240;

    /// <summary>Products per game second of one pair: the slower of native extraction and native furnace processing.</summary>
    public static double PairRate(SmeltingPlan plan, SpatialSnapshot map, ProductionCatalog catalog, NativeFurnace furnace)
    {
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Smelting rate observations span different scopes.");
        var drill = map.Prototypes[map.Items[plan.DrillItem].EntityName];
        string ore = plan.Recipe.Ingredients[0].Name;
        double secondsPerOre = plan.Connection.ResourceIds.Select(id => map.Entities.Single(e => e.Id == id))
            .Select(e => Positive(map.Prototypes[e.Name].MiningTime) / Positive(drill.MiningSpeed)
                / Positive(catalog.Mining[e.Name].Single(p => p.Name == ore && p.DeterministicItem).Amount)).Max();
        double ingredient = Positive(plan.Recipe.Ingredients[0].Amount), product = Positive(plan.Recipe.Products[0].Amount);
        double extracted = 1 / secondsPerOre / ingredient;
        double processed = Positive(furnace.CraftingSpeed) / Positive(plan.Recipe.EnergySeconds);
        return Math.Min(extracted, processed) * product;
    }

    public static int AdditionalPairs(long missing, double pairRate, int pairs)
    {
        if (pairs < 1) throw new ArgumentOutOfRangeException(nameof(pairs));
        if (missing <= 0 || !double.IsFinite(pairRate) || pairRate <= 0) return 0;
        double wanted = Math.Ceiling(missing / (pairRate * TargetGameSeconds));
        return (int)Math.Max(0, Math.Min(wanted, MaximumPairs) - pairs);
    }

    private static double Positive(double? value) => value is { } n && double.IsFinite(n) && n > 0 ? n
        : throw new InvalidDataException("Missing native mining, crafting or recipe rate evidence.");
}
