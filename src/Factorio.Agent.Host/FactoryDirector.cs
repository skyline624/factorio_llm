using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record RawCellCapacity(string Item, string Kind, int Cells, double PerMinute, int Built);
/// <summary>What the resource cells of one raw item deliver: ready capacity, depleted cells and the drills that mine it.</summary>
public sealed record RawSupplyFact(string Item, int ReadyCells, double PerMinute, int DepletedCells, IReadOnlyList<string> Drills);
public sealed record FactoryEquipmentUnlock(string Technology, bool Enabled, bool Available, string[] Equipment);
public sealed record FactoryAutomationReadiness(string[] MissingEquipment, FactoryEquipmentUnlock[] UnlockResearch)
{
    public string Interpretation => "Missing equipment blocks persistent assembler cells. Unlock research is matched by native recipe products and unlock effects, not technology-name guesses. Enabled equipment does not prove installed machines, power or production. Coverage gaps alone do not establish a defense emergency.";
}

/// <summary>Grows the persistent factory: sizes assembler and furnace chains for a target rate, adds laboratories and raw resource cells.</summary>
public sealed class FactoryDirector(IGameClient game, IControllerJournal journal, string directory)
{
    public static readonly string[] MachinePreference = ["assembling-machine-2", "assembling-machine-1"];
    private static readonly string[] AutomationEquipment = ["assembling-machine-1", "inserter", "small-electric-pole", "lab"];

    public static bool Available(ProductionCatalog catalog) =>
        AutomationEquipment.All(item => Enabled(catalog, item));

    public static FactoryAutomationReadiness Readiness(ProductionCatalog catalog, IReadOnlyDictionary<string, NativeTechnology> technologies)
    {
        string[] missing = AutomationEquipment.Where(item => !Enabled(catalog, item)).ToArray();
        var unlocks = technologies.Values.Where(t => !t.Researched).Select(t =>
        {
            var recipes = TechnologyPlanner.RecipeUnlocks(t.Effects).ToHashSet(StringComparer.Ordinal);
            string[] equipment = missing.Where(item => catalog.Recipes.Any(r => recipes.Contains(r.Name)
                && r.Products.Any(p => p.DeterministicItem && p.Name == item))).ToArray();
            return new FactoryEquipmentUnlock(t.Name, t.Enabled, t.Available, equipment);
        }).Where(t => t.Equipment.Length > 0).OrderBy(t => t.Technology, StringComparer.Ordinal).ToArray();
        return new(missing, unlocks);
    }

    public static bool Enabled(ProductionCatalog catalog, string item) =>
        catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));

    /// <summary>Machines new cells are built with: the best enabled assembler, the fastest enabled furnace burning coal and an enabled silo.</summary>
    public static IReadOnlySet<string> MachineItems(ProductionCatalog catalog) =>
        MachinePreference.Where(m => Enabled(catalog, m)).Take(1)
            .Concat(FurnaceCellPlanner.Machine(catalog, FactoryLogistics.Fuel) is { } furnace ? [furnace] : Array.Empty<string>())
            .Concat(SiloCellPlanner.Machines(catalog)).ToHashSet(StringComparer.Ordinal);

    public async Task<AutomationPlan> AutomateAsync(string item, double perMinute, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var machines = MachineItems(catalog);
        var fluidMachines = FluidChainDirector.Machines(catalog);
        // A fluid-unit target keeps the dedicated fluid API; item targets share every solid and chemical intermediate.
        if (!catalog.Items.ContainsKey(item) && FluidChainPlanner.Choose(catalog, item, FluidChainDirector.Machines(catalog)) is not null)
        {
            var chain = await new FluidChainDirector(game, journal, directory).AutomateAsync(item, perMinute, token);
            return new(chain.Stages, chain.RawPerMinute);
        }
        if (machines.Count == 0 && fluidMachines.Count == 0) throw new InvalidOperationException("No factory machine recipe is enabled; research its equipment first.");
        var registry = new FactoryRegistry(directory);
        var registered = (await registry.LoadAsync(catalog.Scope.WorldId, token)).WithTarget(item, perMinute);
        await registry.SaveAsync(registered, token);
        // Every registered target shares the stages: a second science pack adds its gears to the first one's.
        var plan = AutomationPlanner.Plan(catalog, registered.Targets!, machines, fluidMachineItems: fluidMachines);
        await journal.AppendAsync("factory-automation-plan", new { item, perMinute, targets = registered.Targets, plan }, token);
        await SeedRawAsync(catalog, plan.RawPerMinute, token);
        var builder = new FactoryCellBuilder(game, journal, directory);
        var fluids = new FluidChainDirector(game, journal, directory);
        // Consumers after their suppliers keeps early cells useful even if a later build is interrupted.
        foreach (var stage in plan.Stages)
        {
            var before = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var existing = before.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready")
                .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (stage.Kind == FluidCellBuilder.MachineKind)
            {
                await fluids.EnsureStageAsync(stage, catalog, token);
                await StartStageAsync(stage, existing);
                continue;
            }
            var ready = before.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready").Select(c => c.MachineItem).ToArray();
            int missing = AutomationPlanner.MissingMachines(catalog, stage, ready);
            // Power grows before the cells that will draw it, so new machines never brown out the running factory.
            await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync(stage.MachineItem, missing, true, token);
            for (int count = 0; count < missing; count++)
                await builder.BuildAsync(stage.Kind, stage.MachineItem, stage.Recipe, token);
            await StartStageAsync(stage, existing);
        }
        await new FactoryTransportBuilder(game, journal, directory).ConnectAsync(catalog, token: token);
        return plan;

        async Task StartStageAsync(AutomationStage stage, IReadOnlySet<string> existing)
        {
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var added = current.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready"
                && !existing.Contains(c.Id)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (added.Count == 0) return;
            int links = await new FactoryTransportBuilder(game, journal, directory)
                .ConnectAsync(catalog, token: token, targetCellIds: added);
            var startup = await new FactoryLogistics(game, journal, directory)
                .ServiceAsync(FactoryLogistics.MinimumBufferCrafts, token, targetCellIds: added);
            await journal.AppendAsync("factory-stage-startup", new { stage.Recipe, added, links, startup }, token);
        }
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
        await using var startupController = new SpatialController(game, journal);
        for (int built = 0; ; built++)
        {
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            if (item == FactoryLogistics.Fuel)
                foreach (var producer in state.Cells.Where(c => c.Kind == "miner" && c.Recipe == item && c.Status == "ready"))
                    await new CoalProducerStartup(game, journal).StartAsync(producer, catalog, startupController, token);
            var (cells, current) = RawCapacity(state, item);
            if (current >= perMinute - 1e-9 || built >= maximumNewCells)
            {
                var capacity = new RawCellCapacity(item, supply.Kind, cells, current, built);
                await journal.AppendAsync("factory-raw-capacity", new { capacity, perMinute }, token);
                return capacity;
            }
            await builder.BuildNextAsync(item, perMinute - current, token, explorationBudget);
            await ExpandPowerAsync(catalog.Scope, token);
        }
    }

    /// <summary>
    /// Electric resource cells draw from the network like assembler cells, so steam grows after each one. On 2026-10-01
    /// (seed 20261002) dozens of electric drill cells raised the demand to 2.7 times capacity without any expansion attempt.
    /// A failed expansion leaves the built cell in place and is journaled; a changed actor identity still stops the caller.
    /// </summary>
    internal async Task ExpandPowerAsync(ActorScope scope, CancellationToken token)
    {
        try { await new PowerExpansionController(game, journal, directory).EnsureCapacityAsync(0, token); }
        catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
        {
            if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != scope) throw;
            await journal.AppendAsync("power-expansion-failed", new { error = error.GetType().Name, error.Message }, token);
        }
    }

    /// <summary>Resource cells reopened by an attack that one call rebuilds at most; each rebuild may produce its parts.</summary>
    public const int MaximumResumes = 3;

    /// <summary>
    /// Rebuilds resource cells that cell health reopened after a part was destroyed, fewest attempts first. Until now only
    /// growth resumed them: on 2026-10-01 (seed 20261002) nine iron cells stayed open for hours while the mining area was
    /// raided, their capacity uncounted. A failure is journaled and counts one of the cell's attempts, as any build does.
    /// </summary>
    internal async Task<int> ResumeResourceCellsAsync(ActorScope scope, CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var builder = new ResourceCellBuilder(game, journal, directory);
        // A cell where the actor recently died waits for its death zone to expire, like a corpse there. The registry's cells
        // all stand on the first surface (nauvis).
        long tick = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).CollectedTick;
        var zones = game is IDangerZoneReader reader ? await reader.ReadActiveDeathsAsync(scope, 1, tick, token) : [];
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        int resumed = 0;
        for (int call = 0; call < MaximumResumes; call++)
        {
            var state = await registry.LoadAsync(scope.WorldId, token);
            var open = state.Cells
                .Where(c => c.IsResource && c.Status == "building" && c.Attempts < ResourceCellBuilder.MaximumAttempts && c.Recipe is not null && !deferred.Contains(c.Id))
                .OrderBy(c => c.Attempts).ThenBy(c => c.Tick).FirstOrDefault();
            if (open is null) break;
            try
            {
                open = await builder.PrepareResumeAsync(open, state, scope, token);
                if (!ResourceCellBuilder.Safe(open, zones))
                {
                    deferred.Add(open.Id);
                    call--; // A deferred cell spends no reconstruction attempt or resume budget.
                    continue;
                }
                await builder.BuildNextAsync(open.Recipe!, 1, token, explorationBudget: 0, resumeCellId: open.Id);
                resumed++;
            }
            catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
            {
                if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != scope) throw;
                deferred.Add(open.Id);
                await journal.AppendAsync("resource-cell-resume-failed", new { open.Id, open.Recipe, error = error.GetType().Name, error.Message }, token);
            }
        }
        if (resumed > 0) await journal.AppendAsync("resource-cells-resumed", new { resumed }, token);
        return resumed;
    }

    /// <summary>Minutes of the planned rate that carried stock must cover before a raw item can go without a resource cell.</summary>
    public const double SeedHorizonMinutes = 10;

    /// <summary>Resource cells built per raw item and automation call before the assemblers; growth adds the rest later.</summary>
    public const int SeedCellsPerItem = 2;

    /// <summary>
    /// Raw items the plan draws faster than ready resource cells supply and carried stock cannot cover for the horizon, plus coal
    /// for their furnaces when such plates are smelted. A smelter cell costs about what an assembler cell costs and repays it
    /// within minutes, so it is built first; a pocket of plates still lets the assemblers start at once.
    /// </summary>
    internal static IReadOnlyList<(string Item, double PerMinute)> RawSeeds(ProductionCatalog catalog, FactoryState state,
        IReadOnlyDictionary<string, double> raw, IReadOnlyDictionary<string, long> carried)
    {
        bool Unsupplied(string item, double perMinute) => ResourceCellPlanner.Supply(catalog, item) is not null
            && RawCapacity(state, item).PerMinute < perMinute - 1e-9 && carried.GetValueOrDefault(item) < perMinute * SeedHorizonMinutes;
        var seeds = raw.Where(p => p.Value > 0 && Unsupplied(p.Key, p.Value)).Select(p => (p.Key, p.Value)).ToList();
        if (!raw.ContainsKey(FactoryLogistics.Fuel) && Unsupplied(FactoryLogistics.Fuel, RawCapacityGrowth.DefaultPerMinute)
            && seeds.Any(s => ResourceCellPlanner.Supply(catalog, s.Key)?.Kind == "smelter"))
            seeds.Add((FactoryLogistics.Fuel, RawCapacityGrowth.DefaultPerMinute));
        return seeds.OrderBy(s => s.Item1, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Up to two resource cells per undersupplied raw item before any assembler, travelling at most a few steps toward remembered
    /// deposits. A failure leaves the item to the usual growth and procurement; a changed actor identity stays fatal.
    /// </summary>
    private async Task SeedRawAsync(ProductionCatalog catalog, IReadOnlyDictionary<string, double> raw, CancellationToken token)
    {
        var carried = FactoryLogistics.Carried(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token));
        foreach (var (item, perMinute) in RawSeeds(catalog, await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token), raw, carried))
        {
            try
            {
                await EnsureRawAsync(item, Math.Min(perMinute, 10000), token, maximumNewCells: SeedCellsPerItem, explorationBudget: 4);
            }
            catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
            {
                if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != catalog.Scope) throw;
                await journal.AppendAsync("factory-raw-seed-failed", new { item, perMinute, error = error.GetType().Name, error.Message }, token);
            }
        }
    }

    /// <summary>Raw supply per item with resource rows, for the planner to see raw bottlenecks such as depleted patches.</summary>
    public static IReadOnlyList<RawSupplyFact> RawSupply(FactoryState state) => (state.Rows ?? []).Select(r => r.Product)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(item =>
        {
            var (cells, perMinute) = RawCapacity(state, item);
            var resource = state.Cells.Where(c => c.IsResource && c.Recipe == item).ToArray();
            return new RawSupplyFact(item, cells, Math.Round(perMinute, 2), resource.Count(c => c.Status == "depleted"),
                resource.Where(c => c.Status == "ready").Select(c => c.MachineItem).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }).ToArray();

    /// <summary>Ready resource cells producing the item and their summed native rate.</summary>
    public static (int Cells, double PerMinute) RawCapacity(FactoryState state, string item)
    {
        var rows = (state.Rows ?? []).Where(r => r.Product == item).ToDictionary(r => r.Id);
        var ready = state.Cells.Where(c => c.IsResource && c.Status == "ready" && rows.ContainsKey(c.Slot.Band)).ToArray();
        return (ready.Length, ready.Sum(c => rows[c.Slot.Band].CellPerMinute));
    }

}
