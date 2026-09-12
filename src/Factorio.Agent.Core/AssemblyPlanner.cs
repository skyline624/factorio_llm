namespace Factorio.Agent.Core;

public sealed record NativeAssembler(string EntityName, IReadOnlyDictionary<string, bool> Categories,
    double CraftingSpeed, double EnergyPerTick, uint IngredientCount, string? FixedRecipe = null,
    int FluidInputCount = 0, int FluidOutputCount = 0)
{
    public bool Accepts(NativeRecipe recipe) => Categories.ContainsKey(recipe.Category)
        && recipe.Ingredients.Count <= IngredientCount && (FixedRecipe is null || FixedRecipe == recipe.Name)
        && recipe.Ingredients.Count(i => i.Type == "fluid") <= FluidInputCount
        && recipe.Products.Count(i => i.Type == "fluid") <= FluidOutputCount;
}
public sealed record AssemblyPlan(NativeRecipe Recipe, string MachineItem, string? ExistingId = null);

public sealed class AssemblyPlanner
{
    public AssemblyPlan? Choose(string item, IEnumerable<NativeRecipe> recipes,
        IReadOnlyDictionary<string, NativeAssembler> machines, IReadOnlyList<KnownProductionMachine> known, bool configuredOnly = false)
    {
        var plans = new List<AssemblyPlan>();
        foreach (NativeRecipe recipe in recipes.Where(r => r.Enabled && r.Ingredients.Count > 0 && r.Products.Count == 1 && r.Products[0].Name == item
            && r.Products[0].DeterministicItem && r.Ingredients.All(i => (i.DeterministicItem || i.DeterministicFluid) && i.Name != item)))
        {
            foreach (var machine in machines.Where(p => p.Value.Accepts(recipe)))
            {
                var existing = known.Where(k => k.Name == machine.Value.EntityName && k.CanProcess(recipe)
                    && (!configuredOnly || k.Recipe == recipe.Name)).OrderBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault();
                if (!configuredOnly || existing is not null) plans.Add(new(recipe, machine.Key, existing?.Id));
            }
        }
        return plans.OrderByDescending(p => p.ExistingId is not null).ThenBy(p => p.Recipe.Name, StringComparer.Ordinal)
            .ThenBy(p => p.MachineItem, StringComparer.Ordinal).FirstOrDefault();
    }
}

/// <summary>One native photograph separates stocked ingredients, the engaged cycle and completed output.</summary>
public sealed record AssemblyRequirements(IReadOnlyDictionary<string, int> InputsToInsert, long ReadyOutput, bool InProcess,
    IReadOnlyDictionary<string, double> FluidUnitsToSupply)
{
    public static int BatchesAfterTransit(long targetStock, long carried, long outputTransit, double yield, int batchLimit)
    {
        if (targetStock < 0 || carried < 0 || outputTransit < 0 || !double.IsFinite(yield) || yield <= 0 || batchLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(targetStock));
        long missing = Math.Max(0, targetStock - carried);
        if (outputTransit >= missing) return 0;
        return checked((int)Math.Min(batchLimit, Math.Ceiling((missing - outputTransit) / yield)));
    }

    public static AssemblyRequirements From(FactorySnapshot snapshot, string entityId, NativeRecipe recipe, int batches)
    {
        if (batches < 1 || recipe.Products.Count != 1 || !recipe.Products[0].DeterministicItem
            || recipe.Ingredients.Any(i => !(i.DeterministicItem || i.DeterministicFluid) || i.Name == recipe.Products[0].Name))
            throw new InvalidOperationException("Machine accounting requires deterministic ingredients and one distinct solid product.");
        FactoryRecord work = snapshot.Records.SingleOrDefault(r => r.EntityId == entityId && r.Kind == "work")
            ?? throw new InvalidDataException("Missing native work state for the machine.");
        long Count(string identity, string item)
        {
            string inventoryId = work.Data.GetProperty(identity).GetString() ?? throw new InvalidDataException("Missing native inventory identity.");
            FactoryRecord inventory = snapshot.Records.Single(r => r.Id == inventoryId && r.EntityId == entityId && r.Kind == "inventory");
            return inventory.Data.GetProperty("items").TryGetProperty(item, out var count) ? count.GetInt64() : 0;
        }
        long ready = Count("outputInventoryId", recipe.Products[0].Name);
        int outstanding = checked((int)Math.Max(0, batches - Math.Floor(ready / recipe.Products[0].Amount!.Value)));
        var required = MachineInputRequirements.From(snapshot, entityId, recipe, outstanding);
        return new(required.Items, ready, required.InProcess, required.Fluids);
    }
}
