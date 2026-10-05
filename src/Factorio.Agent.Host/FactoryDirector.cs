using System.Text.Json;
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
        return new(missing, EquipmentUnlocks(catalog, technologies, missing));
    }

    internal static FactoryEquipmentUnlock[] EquipmentUnlocks(ProductionCatalog catalog,
        IReadOnlyDictionary<string, NativeTechnology> technologies, IReadOnlyList<string> missing) =>
        technologies.Values.Where(t => !t.Researched).Select(t =>
        {
            var recipes = TechnologyPlanner.RecipeUnlocks(t.Effects).ToHashSet(StringComparer.Ordinal);
            string[] equipment = missing.Where(item => catalog.Recipes.Any(r => recipes.Contains(r.Name)
                && r.Products.Any(p => p.DeterministicItem && p.Name == item))).ToArray();
            return new FactoryEquipmentUnlock(t.Name, t.Enabled, t.Available, equipment);
        }).Where(t => t.Equipment.Length > 0).OrderBy(t => t.Technology, StringComparer.Ordinal).ToArray();

    public static bool Enabled(ProductionCatalog catalog, string item) =>
        catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));

    /// <summary>Machines new cells are built with: the best enabled assembler, the fastest enabled furnace burning coal and an enabled silo.</summary>
    public static IReadOnlySet<string> MachineItems(ProductionCatalog catalog) =>
        MachinePreference.Where(m => Enabled(catalog, m)).Take(1)
            .Concat(FurnaceCellPlanner.Machine(catalog, FactoryLogistics.Fuel) is { } furnace ? [furnace] : Array.Empty<string>())
            .Concat(SiloCellPlanner.Machines(catalog)).ToHashSet(StringComparer.Ordinal);

    public async Task<AutomationPlan> AutomateAsync(string item, double perMinute, CancellationToken token,
        Func<CancellationToken, Task<bool>>? isObjectiveComplete = null)
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
        var plan = AutomationPlanner.Plan(catalog, registered.Targets!, machines, fluidMachineItems: fluidMachines, priorityItem: item);
        await journal.AppendAsync("factory-automation-plan", new { item, perMinute, targets = registered.Targets, plan }, token);
        if (await DeferAsync()) return plan;
        var deferredRaw = await SeedRawAsync(catalog, plan.RawPerMinute, item, token, isObjectiveComplete);
        var builder = new FactoryCellBuilder(game, journal, directory);
        var fluids = new FluidChainDirector(game, journal, directory);
        // Consumers after their suppliers keeps early cells useful even if a later build is interrupted.
        foreach (var stage in plan.Stages)
        {
            if (await DeferAsync()) return plan;
            var before = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var existing = before.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready")
                .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (stage.Kind == FluidCellBuilder.MachineKind)
            {
                await fluids.EnsureStageAsync(stage, catalog, token);
                await StartStageAsync(stage, catalog, existing, token, isObjectiveComplete);
                continue;
            }
            var ready = before.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready").Select(c => c.MachineItem).ToArray();
            int missing = AutomationPlanner.MissingMachines(catalog, stage, ready);
            // Power grows before the cells that will draw it, so new machines never brown out the running factory.
            await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync(stage.MachineItem, missing, true, token);
            for (int count = 0; count < missing; count++)
            {
                if (await DeferAsync())
                {
                    await StartStageAsync(stage, catalog, existing, token, isObjectiveComplete);
                    return plan;
                }
                await builder.BuildAsync(stage.Kind, stage.MachineItem, stage.Recipe, token);
                await ExpandPowerAsync(catalog.Scope, token);
            }
            await StartStageAsync(stage, catalog, existing, token, isObjectiveComplete);
        }
        if (await DeferAsync()) return plan;
        await GrowDeferredRawAsync(catalog, plan.RawPerMinute, deferredRaw, token, isObjectiveComplete);
        if (await DeferAsync()) return plan;
        await new FactoryTransportBuilder(game, journal, directory).ConnectAsync(catalog, token: token);
        return plan;

        async Task<bool> DeferAsync()
        {
            if (isObjectiveComplete is null || !await isObjectiveComplete(token)) return false;
            await journal.AppendAsync("factory-preparation-deferred", new { item, perMinute, reason = "caller-objective-completed" }, token);
            return true; // Targets and completed cells stay registered; a later goal can continue their construction.
        }

    }

    /// <summary>Refills this stage before downstream construction, including suppliers retained from a previous call.</summary>
    internal async Task StartStageAsync(AutomationStage stage, ProductionCatalog catalog, IReadOnlySet<string> existing,
        CancellationToken token, Func<CancellationToken, Task<bool>>? isObjectiveComplete = null)
    {
        var current = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var serviced = current.Cells.Where(c => c.Kind == stage.Kind && c.Recipe == stage.Recipe && c.Status == "ready")
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (serviced.Count == 0) return;
        var added = serviced.Where(id => !existing.Contains(id)).ToHashSet(StringComparer.Ordinal);
        int links = await new FactoryTransportBuilder(game, journal, directory)
            .ConnectAsync(catalog, token: token, targetCellIds: serviced);
        var startup = await new FactoryLogistics(game, journal, directory)
            .ServiceAsync(FactoryLogistics.MinimumBufferCrafts, token, usePlannedBuffers: true, targetCellIds: serviced);
        var initialStartup = startup;
        // A stage's input can be belt-covered even though its raw supplier ran out of coal during construction.
        // Pay one bounded ignition shortfall before building downstream consumers; existing stocks still fill reserves.
        StockGoalResult? fuelProcurement = null;
        long fuelMissing = startup.FuelShortfall;
        if (fuelMissing > 0 && (isObjectiveComplete is null || !await isObjectiveComplete(token)))
        {
            var stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (stock.Scope != catalog.Scope) throw new InvalidDataException("Actor changed before stage fuel procurement.");
            int target = (int)Math.Min(1000, FactoryLogistics.Carried(stock).GetValueOrDefault(FactoryLogistics.Fuel) + Math.Min(fuelMissing, 400));
            using (ProductionReservations.EnterFactory(await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token)))
                fuelProcurement = await new ProductionGoalExecutor(game, journal).RunAsync(FactoryLogistics.Fuel, target, token);
            stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (stock.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during stage fuel procurement.");
            startup = await new FactoryLogistics(game, journal, directory)
                .ServiceAsync(FactoryLogistics.MinimumBufferCrafts, token, usePlannedBuffers: true, targetCellIds: serviced);
        }
        await journal.AppendAsync("factory-stage-startup", new { stage.Recipe, added, serviced, links, initialStartup, fuelProcurement, startup }, token);
    }

    public async Task<int> EnsureLabsAsync(int count, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        int existing = state.Cells.Count(c => c.Kind == "lab" && c.Status == "ready");
        if (existing < count)
        {
            var stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (stock.Scope != catalog.Scope) throw new InvalidDataException("Actor scope changed while finding reusable laboratories.");
            var claimed = state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
            var candidates = stock.Records.Where(r => r.Kind == "entity" && r.Name == (catalog.Items["lab"].PlaceEntity ?? "lab")
                && r.Data.GetProperty("role").GetString() == "factory" && !claimed.Contains(r.EntityId)
                && FactoryPower.IsFed(stock, r.EntityId) == true).OrderBy(r => r.EntityId, StringComparer.Ordinal).ToArray();
            string[] poles = catalog.Items.Where(p => p.Value.PlaceEntityType == "electric-pole").Select(p => p.Key).ToArray();
            await using var controller = new SpatialController(game, journal);
            foreach (var candidate in candidates)
            {
                if (existing >= count) break;
                await controller.TravelAsync(candidate.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, 6, catalog, token);
                var map = await new SpatialClient(game).CaptureAsync(["lab", .. poles], 32, token);
                stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                var adopted = FactoryLaboratoryAdoption.Plan(state, stock, map, catalog, candidate.EntityId);
                if (adopted is null) continue;
                await registry.SaveAsync(state.With(adopted), token);
                existing++;
                await journal.AppendAsync("factory-laboratory-adopted", adopted, token);
            }
        }
        await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync("lab", count - existing, false, token);
        for (int built = existing; built < count; built++)
        {
            await new FactoryCellBuilder(game, journal, directory).BuildAsync("lab", "lab", null, token);
            await ExpandPowerAsync(catalog.Scope, token);
        }
        return Math.Max(existing, count);
    }

    /// <summary>
    /// Adds miner or smelter cells on ore patches until ready cells cover the rate. Each cell's rate comes from native
    /// drill, furnace and inserter speeds; at most maximumNewCells are built per call.
    /// </summary>
    public async Task<RawCellCapacity> EnsureRawAsync(string item, double perMinute, CancellationToken token,
        int maximumNewCells = ResourceCellPlanner.MaximumRowCells, int explorationBudget = 16,
        Func<CancellationToken, Task<bool>>? isObjectiveComplete = null)
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
            if (isObjectiveComplete is not null && await isObjectiveComplete(token))
            {
                var existing = RawCapacity(state, item);
                return new(item, supply.Kind, existing.Cells, existing.PerMinute, built);
            }
            await new ResourceCellStartup(game, journal).StartManyAsync(
                state.Cells.Where(c => c.IsResource && c.Recipe == item && c.Status == "ready"),
                catalog, startupController, token, isObjectiveComplete);
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

    /// <summary>Minutes of the planned rate that distributable native stock must cover before raw growth can wait.</summary>
    public const double SeedHorizonMinutes = ResourceCellPlanner.MinimumSupplyMinutes;

    /// <summary>Resource cells built per raw item and automation call before the assemblers; growth adds the rest later.</summary>
    public const int SeedCellsPerItem = 2;
    /// <summary>Additional coal suppliers per call when the joint boiler plan lacks local, unpromised sources.</summary>
    public const int PowerFuelSeedCells = 2;

    /// <summary>
    /// Raw items the plan draws faster than ready resource cells supply and distributable stock cannot cover for the horizon,
    /// plus coal for new or retained active smelters. Explicit raw-rate targets require capacity; unrelated ones with stock
    /// covering the horizon can grow after the requested chain starts. A requested raw-rate target still grows first.
    /// A smelter cell costs about what an assembler cell costs and repays it
    /// within minutes, so it is built first; a pocket of plates still lets the assemblers start at once.
    /// </summary>
    internal static IReadOnlyList<(string Item, double PerMinute)> RawSeeds(ProductionCatalog catalog, FactoryState state,
        IReadOnlyDictionary<string, double> raw, IReadOnlyDictionary<string, long> available, double powerFuelPerMinute = 0,
        string? priorityItem = null)
    {
        if (!double.IsFinite(powerFuelPerMinute) || powerFuelPerMinute < 0) throw new ArgumentOutOfRangeException(nameof(powerFuelPerMinute));
        if (priorityItem is not null && state.Targets?.ContainsKey(priorityItem) != true)
            throw new ArgumentException("Raw preparation priority must be a registered target.", nameof(priorityItem));
        bool Unsupplied(string item, double perMinute) => ResourceCellPlanner.Supply(catalog, item) is not null
            && RawCapacity(state, item).PerMinute < perMinute - 1e-9
            && (state.Targets?.ContainsKey(item) == true && (priorityItem is null || priorityItem == item)
                || available.GetValueOrDefault(item) < perMinute * SeedHorizonMinutes);
        var seeds = raw.Where(p => p.Value > 0 && Unsupplied(p.Key, p.Value)).Select(p => (p.Key, p.Value)).ToList();
        // Existing plate capacity still consumes fuel. On normal seed20261072, coal depletion was missed until
        // another plate cell was needed, leaving preparation to repeated transient coal procurement.
        bool smelting = seeds.Any(s => ResourceCellPlanner.Supply(catalog, s.Key)?.Kind == "smelter")
            || RawStartupCells(catalog, state, raw).Any(c => c.Kind == "smelter");
        if (!raw.ContainsKey(FactoryLogistics.Fuel) && Unsupplied(FactoryLogistics.Fuel, RawCapacityGrowth.DefaultPerMinute)
            && smelting)
            seeds.Add((FactoryLogistics.Fuel, RawCapacityGrowth.DefaultPerMinute));
        if (powerFuelPerMinute > 0)
        {
            double fuelRate = powerFuelPerMinute + Math.Max(raw.GetValueOrDefault(FactoryLogistics.Fuel),
                smelting ? RawCapacityGrowth.DefaultPerMinute : 0);
            seeds.RemoveAll(s => s.Item1 == FactoryLogistics.Fuel);
            // Carried coal can start equipment, but cannot replace sustained boiler supply capacity.
            if (RawCapacity(state, FactoryLogistics.Fuel).PerMinute < fuelRate - 1e-9)
                seeds.Add((FactoryLogistics.Fuel, fuelRate));
        }
        return seeds.OrderBy(s => s.Item1, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Up to two resource cells per undersupplied raw item before any assembler, travelling at most a few steps toward remembered
    /// deposits. A failure leaves the item to the usual growth and procurement; a changed actor identity stays fatal.
    /// </summary>
    private async Task<IReadOnlySet<string>> SeedRawAsync(ProductionCatalog catalog, IReadOnlyDictionary<string, double> raw,
        string priorityItem, CancellationToken token,
        Func<CancellationToken, Task<bool>>? isObjectiveComplete)
    {
        // Capacity includes an idle ready cell: start retained suppliers before deciding that no raw growth is needed.
        var registry = new FactoryRegistry(directory);
        // A connected electric drill is idle when retained boiler feeders ran dry during the previous long goal.
        // Restore their paid fuel before starting or extending raw suppliers.
        await ExpandPowerAsync(catalog.Scope, token);
        var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
        PowerState? power = current.Cells.Any(c => c.Kind == "power" && c.Status == "ready")
            ? await new PowerExpansionController(game, journal, directory).ObserveAsync(token) : null;
        double powerFuel = current.Cells.Sum(c => PowerFuelPolicy.Demand(catalog, c, FactoryLogistics.Fuel, power) ?? 0);
        var startupRaw = new Dictionary<string, double>(raw, StringComparer.Ordinal);
        if (powerFuel > 0) startupRaw[FactoryLogistics.Fuel] = startupRaw.GetValueOrDefault(FactoryLogistics.Fuel) + powerFuel;
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Raw preparation stock scope changed.");
        var deferredStartup = DeferredRawStartupItems(catalog, current, startupRaw, FactoryLogistics.AvailableStock(snapshot),
            priorityItem, powerFuel);
        await journal.AppendAsync("factory-raw-startup-deferred", new { priorityItem, snapshot.Scope, snapshot.CollectedTick,
            items = deferredStartup.Order(StringComparer.Ordinal) }, token);
        await using (var controller = new SpatialController(game, journal))
            await new ResourceCellStartup(game, journal).StartManyAsync(
                RawStartupCells(catalog, current, startupRaw).Where(c => !deferredStartup.Contains(c.Recipe!)),
                catalog, controller, token, isObjectiveComplete);
        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Raw preparation stock scope changed.");
        var available = FactoryLogistics.AvailableStock(snapshot);
        current = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var seeds = RawSeeds(catalog, current, raw, available, powerFuel, priorityItem);
        var first = seeds.Select(s => s.Item).ToHashSet(StringComparer.Ordinal);
        var deferred = RawSeeds(catalog, current, raw, available, powerFuel).Where(s => !first.Contains(s.Item))
            .Select(s => s.Item).ToHashSet(StringComparer.Ordinal);
        deferred.UnionWith(deferredStartup);
        await journal.AppendAsync("factory-raw-preparation", new { priorityItem, snapshot.Scope, snapshot.CollectedTick,
            available = raw.Keys.Append(FactoryLogistics.Fuel).Distinct(StringComparer.Ordinal)
                .ToDictionary(item => item, item => available.GetValueOrDefault(item), StringComparer.Ordinal),
            seeds = seeds.Select(s => new { s.Item, s.PerMinute }), deferred = deferred.Order(StringComparer.Ordinal) }, token);
        if (!await EnsureRawSeedsAsync(catalog, seeds, token, isObjectiveComplete)) return deferred;
        await new PowerFuelTransport(game, journal, directory).ConnectAsync(catalog, token);
        await SeedPowerFuelSourcesAsync(catalog, token, isObjectiveComplete);
        return deferred;
    }

    private async Task GrowDeferredRawAsync(ProductionCatalog catalog, IReadOnlyDictionary<string, double> raw,
        IReadOnlySet<string> deferred, CancellationToken token, Func<CancellationToken, Task<bool>>? isObjectiveComplete)
    {
        if (deferred.Count == 0) return;
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Deferred raw growth stock scope changed.");
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        await using (var controller = new SpatialController(game, journal))
            await new ResourceCellStartup(game, journal).StartManyAsync(
                RawStartupCells(catalog, state, raw).Where(c => deferred.Contains(c.Recipe!)),
                catalog, controller, token, isObjectiveComplete);
        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Deferred raw growth stock scope changed.");
        state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var seeds = RawSeeds(catalog, state, raw, FactoryLogistics.AvailableStock(snapshot))
            .Where(s => deferred.Contains(s.Item)).ToArray();
        await journal.AppendAsync("factory-raw-growth", new { snapshot.Scope, snapshot.CollectedTick,
            seeds = seeds.Select(s => new { s.Item, s.PerMinute }) }, token);
        await EnsureRawSeedsAsync(catalog, seeds, token, isObjectiveComplete);
    }

    private async Task<bool> EnsureRawSeedsAsync(ProductionCatalog catalog, IReadOnlyList<(string Item, double PerMinute)> seeds,
        CancellationToken token, Func<CancellationToken, Task<bool>>? isObjectiveComplete)
    {
        foreach (var (item, perMinute) in seeds)
        {
            if (isObjectiveComplete is not null && await isObjectiveComplete(token)) return false;
            try
            {
                await EnsureRawAsync(item, Math.Min(perMinute, 10000), token, maximumNewCells: SeedCellsPerItem, explorationBudget: 4,
                    isObjectiveComplete: isObjectiveComplete);
            }
            catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
            {
                if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != catalog.Scope) throw;
                await journal.AppendAsync("factory-raw-seed-failed", new { item, perMinute, error = error.GetType().Name, error.Message }, token);
            }
        }
        return true;
    }

    internal async Task SeedPowerFuelSourcesAsync(ProductionCatalog catalog, CancellationToken token,
        Func<CancellationToken, Task<bool>>? isObjectiveComplete = null)
    {
        // Early burner-only factories keep the existing shared-source path and raw-growth budget.
        if (!Enabled(catalog, "electric-mining-drill")) return;
        var transport = new PowerFuelTransport(game, journal, directory);
        for (int built = 0; built < PowerFuelSeedCells; built++)
        {
            if (isObjectiveComplete is not null && await isObjectiveComplete(token)) return;
            var need = await transport.ObserveSourceNeedAsync(catalog, token);
            if (need.Missing == 0) return;
            try
            {
                var cell = await new ResourceCellBuilder(game, journal, directory).BuildNextAsync(FactoryLogistics.Fuel,
                    need.PerMinute, token, explorationBudget: 4);
                await ExpandPowerAsync(catalog.Scope, token);
                await journal.AppendAsync("power-fuel-source-added", new { need, cell.Id, cell.Entities, cell.Tick }, token);
            }
            catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
            {
                if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != catalog.Scope) throw;
                await journal.AppendAsync("power-fuel-source-seed-failed", new { need, error = error.GetType().Name, error.Message }, token);
                return;
            }
        }
    }

    internal static IReadOnlyList<FactoryCell> RawStartupCells(ProductionCatalog catalog, FactoryState state,
        IReadOnlyDictionary<string, double> raw)
    {
        var wanted = raw.Where(p => p.Value > 0).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (wanted.Any(item => ResourceCellPlanner.Supply(catalog, item)?.Kind == "smelter")) wanted.Add(FactoryLogistics.Fuel);
        return state.Cells.Where(c => c.IsResource && c.Status == "ready" && c.Recipe is not null && wanted.Contains(c.Recipe))
            .OrderBy(c => c.Recipe == FactoryLogistics.Fuel ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Buffered suppliers can restart after the requested chain; its raw-rate target and boiler fuel cannot wait.</summary>
    internal static IReadOnlySet<string> DeferredRawStartupItems(ProductionCatalog catalog, FactoryState state,
        IReadOnlyDictionary<string, double> raw, IReadOnlyDictionary<string, long> available, string priorityItem,
        double powerFuelPerMinute)
    {
        if (!double.IsFinite(powerFuelPerMinute) || powerFuelPerMinute < 0) throw new ArgumentOutOfRangeException(nameof(powerFuelPerMinute));
        return RawStartupCells(catalog, state, raw).Select(c => c.Recipe!).Distinct(StringComparer.Ordinal)
            .Where(item => item != priorityItem && !(item == FactoryLogistics.Fuel && powerFuelPerMinute > 0)
                && available.GetValueOrDefault(item) >= (raw.GetValueOrDefault(item) is > 0 and var rate
                    ? rate : RawCapacityGrowth.DefaultPerMinute) * SeedHorizonMinutes)
            .ToHashSet(StringComparer.Ordinal);
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
