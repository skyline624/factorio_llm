using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record RawCellCapacity(string Item, string Kind, int Cells, double PerMinute, int Built);

/// <summary>Grows the persistent factory: sizes assembler and furnace chains for a target rate, adds laboratories and raw resource cells.</summary>
public sealed class FactoryDirector(IGameClient game, IControllerJournal journal, string directory)
{
    public static readonly string[] MachinePreference = ["assembling-machine-2", "assembling-machine-1"];

    public static bool Available(ProductionCatalog catalog) =>
        new[] { "assembling-machine-1", "inserter", "small-electric-pole", "lab" }.All(item => Enabled(catalog, item));

    public static bool Enabled(ProductionCatalog catalog, string item) =>
        catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));

    /// <summary>Machines new cells are built with: the best enabled assembler and the fastest enabled furnace burning coal.</summary>
    public static IReadOnlySet<string> MachineItems(ProductionCatalog catalog) =>
        MachinePreference.Where(m => Enabled(catalog, m)).Take(1)
            .Concat(FurnaceCellPlanner.Machine(catalog, FactoryLogistics.Fuel) is { } furnace ? [furnace] : Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

    public async Task<AutomationPlan> AutomateAsync(string item, double perMinute, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var machines = MachineItems(catalog);
        if (machines.Count == 0) throw new InvalidOperationException("No assembling machine or furnace recipe is enabled; research automation first.");
        var plan = AutomationPlanner.Plan(catalog, item, perMinute, machines);
        await journal.AppendAsync("factory-automation-plan", new { item, perMinute, plan }, token);
        var registry = new FactoryRegistry(directory);
        var builder = new FactoryCellBuilder(game, journal, directory);
        // Consumers after their suppliers keeps early cells useful even if a later build is interrupted.
        foreach (var stage in plan.Stages.OrderBy(s => Depth(catalog, s.Recipe, machines)))
        {
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            int existing = state.Cells.Count(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready");
            // Power grows before the cells that will draw it, so new machines never brown out the running factory.
            await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync(stage.MachineItem, stage.Machines - existing, true, token);
            for (int count = existing; count < stage.Machines; count++)
                await builder.BuildAsync(stage.Kind, stage.MachineItem, stage.Recipe, token);
        }
        return plan;
    }

    public async Task<int> EnsureLabsAsync(int count, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        int existing = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Count(c => c.Kind == "lab" && c.Status == "ready");
        await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync("lab", count - existing, false, token);
        for (int built = existing; built < count; built++)
            await new FactoryCellBuilder(game, journal, directory).BuildAsync("lab", "lab", null, token);
        return Math.Max(existing, count);
    }

    /// <summary>
    /// Adds miner or smelter cells on ore patches until ready cells cover the rate. Each cell's rate comes from native
    /// drill, furnace and inserter speeds; at most maximumNewCells are built per call.
    /// </summary>
    public async Task<RawCellCapacity> EnsureRawAsync(string item, double perMinute, CancellationToken token,
        int maximumNewCells = ResourceCellPlanner.MaximumRowCells, int explorationBudget = 16)
    {
        if (!double.IsFinite(perMinute) || perMinute <= 0 || perMinute > 10000) throw new ArgumentOutOfRangeException(nameof(perMinute));
        if (maximumNewCells is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumNewCells));
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var supply = ResourceCellPlanner.Supply(catalog, item)
            ?? throw new InvalidOperationException($"{item} is neither mined nor smelted from a single ore.");
        var registry = new FactoryRegistry(directory);
        var builder = new ResourceCellBuilder(game, journal, directory);
        for (int built = 0; ; built++)
        {
            var (cells, current) = RawCapacity(await registry.LoadAsync(catalog.Scope.WorldId, token), item);
            if (current >= perMinute - 1e-9 || built >= maximumNewCells)
            {
                var capacity = new RawCellCapacity(item, supply.Kind, cells, current, built);
                await journal.AppendAsync("factory-raw-capacity", new { capacity, perMinute }, token);
                return capacity;
            }
            await builder.BuildNextAsync(item, perMinute - current, token, explorationBudget);
        }
    }

    /// <summary>Ready resource cells producing the item and their summed native rate.</summary>
    public static (int Cells, double PerMinute) RawCapacity(FactoryState state, string item)
    {
        var rows = (state.Rows ?? []).Where(r => r.Product == item).ToDictionary(r => r.Id);
        var ready = state.Cells.Where(c => c.IsResource && c.Status == "ready" && rows.ContainsKey(c.Slot.Band)).ToArray();
        return (ready.Length, ready.Sum(c => rows[c.Slot.Band].CellPerMinute));
    }

    private static int Depth(ProductionCatalog catalog, string recipeName, IReadOnlySet<string> machines, int guard = 0)
    {
        if (guard > 16) return guard;
        var recipe = catalog.Recipes.Single(r => r.Name == recipeName);
        return recipe.Ingredients.Select(i => AutomationPlanner.Choose(catalog, i.Name, machines))
            .Where(c => c is not null).Select(c => 1 + Depth(catalog, c!.Value.Recipe.Name, machines, guard + 1)).DefaultIfEmpty(0).Max();
    }
}
