namespace Factorio.Agent.Core;

public sealed record FluidProductionPlan(NativeRecipe Recipe, string MachineItem, string? ExistingId);

/// <summary>Selects supported native fluid conversions without silently substituting another recipe.</summary>
public sealed class FluidProductionPlanner
{
    public FluidProductionPlan? Choose(string fluid, ProductionCatalog catalog, IReadOnlyList<KnownProductionMachine> known)
    {
        var plans = new List<FluidProductionPlan>();
        foreach (var recipe in catalog.Recipes.Where(r => r.Enabled && r.Ingredients.Count > 0 && r.Products.Count == 1
            && r.Products[0].DeterministicFluid && r.Products[0].Name == fluid
            && r.Ingredients.All(i => (i.DeterministicItem || i.DeterministicFluid) && i.Name != fluid)))
            foreach (var machine in catalog.Assemblers ?? new Dictionary<string, NativeAssembler>())
                if (machine.Value.Accepts(recipe))
                    plans.Add(new(recipe, machine.Key, known.Where(k => k.Name == machine.Value.EntityName && k.CanProcess(recipe))
                        .OrderByDescending(k => k.Recipe == recipe.Name).ThenBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault()?.Id));
        return plans.OrderByDescending(p => p.ExistingId is not null).ThenBy(p => p.Recipe.Name, StringComparer.Ordinal)
            .ThenBy(p => p.MachineItem, StringComparer.Ordinal).FirstOrDefault();
    }
}
