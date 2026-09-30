using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Grows the persistent factory: sizes assembler chains for a target rate and adds laboratories.</summary>
public sealed class FactoryDirector(IGameClient game, IControllerJournal journal, string directory)
{
    public static readonly string[] MachinePreference = ["assembling-machine-2", "assembling-machine-1"];

    public static bool Available(ProductionCatalog catalog) =>
        new[] { "assembling-machine-1", "inserter", "small-electric-pole", "lab" }.All(item => Enabled(catalog, item));

    public static bool Enabled(ProductionCatalog catalog, string item) =>
        catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));

    public async Task<AutomationPlan> AutomateAsync(string item, double perMinute, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var machines = MachinePreference.Where(m => Enabled(catalog, m)).Take(1).ToHashSet(StringComparer.Ordinal);
        if (machines.Count == 0) throw new InvalidOperationException("No assembling machine recipe is enabled; research automation first.");
        var plan = AutomationPlanner.Plan(catalog, item, perMinute, machines);
        await journal.AppendAsync("factory-automation-plan", new { item, perMinute, plan }, token);
        var registry = new FactoryRegistry(directory);
        var builder = new FactoryCellBuilder(game, journal, directory);
        // Consumers after their suppliers keeps early cells useful even if a later build is interrupted.
        foreach (var stage in plan.Stages.OrderBy(s => Depth(catalog, s.Recipe, machines)))
        {
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            int existing = state.Cells.Count(c => c.Kind == "assembler" && c.Recipe == stage.Recipe && c.Status == "ready");
            for (int count = existing; count < stage.Machines; count++)
                await builder.BuildAsync("assembler", stage.MachineItem, stage.Recipe, token);
        }
        return plan;
    }

    public async Task<int> EnsureLabsAsync(int count, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        int existing = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Count(c => c.Kind == "lab" && c.Status == "ready");
        for (int built = existing; built < count; built++)
            await new FactoryCellBuilder(game, journal, directory).BuildAsync("lab", "lab", null, token);
        return Math.Max(existing, count);
    }

    private static int Depth(ProductionCatalog catalog, string recipeName, IReadOnlySet<string> machines, int guard = 0)
    {
        if (guard > 16) return guard;
        var recipe = catalog.Recipes.Single(r => r.Name == recipeName);
        return recipe.Ingredients.Select(i => AutomationPlanner.Choose(catalog, i.Name, machines))
            .Where(c => c is not null).Select(c => 1 + Depth(catalog, c!.Value.Recipe.Name, machines, guard + 1)).DefaultIfEmpty(0).Max();
    }
}
