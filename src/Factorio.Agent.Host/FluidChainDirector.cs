using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Grows persistent fluid chains stage by stage, suppliers first. A stage consuming an extracted fluid is
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
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        var plan = FluidChainPlanner.Plan(catalog, item, perMinute, Machines(catalog, carried))
            ?? throw new InvalidOperationException($"{item} has no enabled recipe chain through researched or carried fluid machines.");
        await journal.AppendAsync("fluid-chain-plan", new { item, perMinute, plan }, token);
        foreach (var stage in plan.Stages)
            await EnsureStageAsync(stage, catalog, token);
        return plan;
    }

    /// <summary>A shared stage of the whole-factory plan, including its paired extraction and cold-start logistics.</summary>
    internal async Task EnsureStageAsync(AutomationStage stage, ProductionCatalog catalog, CancellationToken token)
    {
        var recipe = catalog.Recipes.Single(r => r.Name == stage.Recipe);
        bool io = recipe.Ingredients.Any(i => i.DeterministicItem) || recipe.Products.Any(p => p.DeterministicItem);
        var sources = recipe.Ingredients.Where(i => i.DeterministicFluid && FluidChainPlanner.Resource(catalog, i.Name) is not null).ToArray();
        if (sources.Length > 1) throw new InvalidOperationException("A fluid cell supports one paired extracted fluid.");
        var source = sources.Length == 0 ? null : new FluidSource(sources[0].Name, FluidChainPlanner.Resource(catalog, sources[0].Name),
            sources[0].Amount!.Value * stage.CraftsPerMinute);
        var builder = new FluidCellBuilder(game, journal, directory);
        var power = new PowerExpansionController(game, journal, directory);
        var registry = new FactoryRegistry(directory);
        for (int added = 0; ; added++)
        {
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var ready = state.Cells.Where(c => c.Kind == FluidCellBuilder.MachineKind && c.Recipe == stage.Recipe && c.Status == "ready")
                .Select(c => c.MachineItem).ToArray();
            int machines = ready.Length;
            int wanted = machines + AutomationPlanner.MissingMachines(catalog, stage, ready);
            var rates = source is null ? null : await builder.ExtractorRatesAsync(source.Fluid, token);
            var next = rates is null ? (Extractor: false, Machine: machines < wanted)
                : NextPair(machines, wanted, rates.Count, rates.Values.Sum(), source!.UnitsPerMinute);
            if (!next.Machine) break;
            if (added >= MaximumNewMachines || machines >= MaximumNewMachines)
            {
                await journal.AppendAsync("fluid-chain-stage-short", new { stage, machines, extraction = rates?.Values.Sum(), source }, token);
                break;
            }
            if (next.Extractor) await builder.BuildExtractorAsync(source!.Resource!, source.Fluid, token);
            await power.EnsureCapacityForCellsAsync(stage.MachineItem, 1, io, token);
            await builder.BuildMachineAsync(stage.MachineItem, stage.Recipe, token);
        }
        if (recipe.Products[0].DeterministicFluid && recipe.Ingredients.Any(i => i.DeterministicItem))
            await PrimeFluidAsync(recipe, catalog, token);
    }

    /// <summary>Solid-fed fluid stages must actually produce before the next builder waits for their fluid stock.</summary>
    private async Task PrimeFluidAsync(NativeRecipe recipe, ProductionCatalog catalog, CancellationToken token)
    {
        var reader = new FactorySnapshotClient(game);
        var registry = new FactoryRegistry(directory);
        var logistics = new FactoryLogistics(game, journal, directory);
        var executor = new ProductionGoalExecutor(game, journal);
        await using var controller = new SpatialController(game, journal);
        for (int round = 0; round < 30; round++)
        {
            var snapshot = await reader.CaptureAsync(cancellationToken: token);
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while priming a fluid supplier.");
            double amount = snapshot.SummarizeStocks().Fluids.GetValueOrDefault(recipe.Products[0].Name);
            if (amount > 0)
            {
                await journal.AppendAsync("fluid-stage-primed", new { recipe.Name, fluid = recipe.Products[0].Name, amount, round, snapshot.CollectedTick }, token);
                return;
            }
            var service = await logistics.ServiceAsync(5, token);
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var producers = state.Cells.Where(c => c.Status == "ready" && c.Recipe is not null)
                .Select(c => catalog.Recipes.FirstOrDefault(r => r.Name == c.Recipe)?.Products[0].Name ?? c.Recipe!)
                .ToHashSet(StringComparer.Ordinal);
            // A registered producer gets subsequent collection rounds. Only missing external solid inputs are procured.
            foreach (var ingredient in recipe.Ingredients.Where(i => i.DeterministicItem && !producers.Contains(i.Name)))
            {
                long missing = service.Shortfall.GetValueOrDefault(ingredient.Name);
                if (missing <= 0) continue;
                var carriedSnapshot = await reader.CaptureAsync(cancellationToken: token);
                if (carriedSnapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed before procuring a fluid supplier's solid inputs.");
                var carried = FactoryLogistics.Carried(carriedSnapshot);
                int target = checked((int)Math.Min(1000, carried.GetValueOrDefault(ingredient.Name) + Math.Min(missing, 40 * ingredient.Amount!.Value)));
                using (ProductionReservations.EnterFactory(state))
                    await executor.RunAsync(ingredient.Name, target, token);
            }
            var waited = await controller.WorkAsync("wait", new { ticks = 120 }, 420, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Fluid supplier priming wait did not complete; reconcile before resuming.");
        }
        throw new InvalidOperationException($"The solid-fed {recipe.Name} stage did not produce native fluid within its priming budget.");
    }

    /// <summary>Machines a chain may use, those with fluid boxes among them: craftable from an enabled recipe, or already carried.</summary>
    public static IReadOnlySet<string> Machines(ProductionCatalog catalog, IReadOnlyDictionary<string, long>? carried = null) =>
        (catalog.Assemblers ?? new Dictionary<string, NativeAssembler>()).Keys
            .Where(m => FactoryDirector.Enabled(catalog, m) || carried?.GetValueOrDefault(m) > 0).ToHashSet(StringComparer.Ordinal);

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
