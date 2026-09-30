using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Grows persistent fluid chains (plastic, sulfur) stage by stage, suppliers first. A stage consuming an extracted fluid is
/// built as extractor and machine pairs until both its planned machine count and the extractors' native deposit yield cover
/// the plan; solid ingredients and products stay with factory logistics.
/// </summary>
public sealed class FluidChainDirector(IGameClient game, IControllerJournal journal, string directory)
{
    /// <summary>Machines added to one stage per call; a supply still short afterwards is journaled, not chased.</summary>
    public const int MaximumNewMachines = 8;

    public async Task<FluidChainPlan> AutomateAsync(string item, double perMinute, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var plan = FluidChainPlanner.Plan(catalog, item, perMinute)
            ?? throw new InvalidOperationException($"{item} has no enabled recipe chain through fluid machines.");
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        string[] locked = plan.Stages.Select(s => s.MachineItem).Distinct(StringComparer.Ordinal)
            .Where(m => !FactoryDirector.Enabled(catalog, m) && carried.GetValueOrDefault(m) == 0).ToArray();
        if (locked.Length > 0) throw new InvalidOperationException($"Research the recipes of {string.Join(", ", locked)} before automating {item}.");
        await journal.AppendAsync("fluid-chain-plan", new { item, perMinute, plan }, token);
        var builder = new FluidCellBuilder(game, journal, directory);
        var power = new PowerExpansionController(game, journal, directory);
        var registry = new FactoryRegistry(directory);
        foreach (var stage in plan.Stages)
        {
            var recipe = catalog.Recipes.Single(r => r.Name == stage.Recipe);
            bool io = recipe.Ingredients.Any(i => i.DeterministicItem) || recipe.Products.Any(p => p.DeterministicItem);
            var source = plan.Sources.FirstOrDefault(s => s.Resource is not null && recipe.Ingredients.Any(i => i.Name == s.Fluid));
            for (int added = 0; ; added++)
            {
                var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                int machines = state.Cells.Count(c => c.Kind == FluidCellBuilder.MachineKind && c.Recipe == stage.Recipe && c.Status == "ready");
                var rates = source is null ? null : await builder.ExtractorRatesAsync(source.Fluid, token);
                var next = rates is null ? (Extractor: false, Machine: machines < stage.Machines)
                    : NextPair(machines, stage.Machines, rates.Count, rates.Values.Sum(), source!.UnitsPerMinute);
                if (!next.Machine) break;
                if (added >= MaximumNewMachines)
                {
                    await journal.AppendAsync("fluid-chain-stage-short", new { stage, machines, extraction = rates?.Values.Sum(), source }, token);
                    break;
                }
                if (next.Extractor) await builder.BuildExtractorAsync(source!.Resource!, source.Fluid, token);
                // Power grows before the machine that will draw it.
                await power.EnsureCapacityForCellsAsync(stage.MachineItem, 1, io, token);
                await builder.BuildMachineAsync(stage.MachineItem, stage.Recipe, token);
            }
        }
        return plan;
    }

    /// <summary>
    /// The next build of a stage fed by extractors, one extractor per machine: nothing once the machines and the extracted
    /// capacity both cover the plan, a machine alone while a free extractor waits, otherwise an extractor and its machine.
    /// </summary>
    public static (bool Extractor, bool Machine) NextPair(int machines, int wantedMachines, int extractors, double capacity, double demand)
    {
        if (machines >= wantedMachines && capacity >= demand - 1e-9) return (false, false);
        return (extractors <= machines, true);
    }
}
