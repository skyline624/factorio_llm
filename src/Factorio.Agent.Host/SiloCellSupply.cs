using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Keeps a registered silo cell working toward a launch. Only the cell's inserter moves ingredients into the silo: factory logistics
/// restocks its chest, as it restocks every planned cell, and its maintenance rebuilds destroyed roles. One instance tends one launch,
/// remembering when logistics last ran and how long the cell has shown no progress.
/// </summary>
internal sealed class SiloCellSupply(IGameClient game, IControllerJournal journal, string directory)
{
    /// <summary>Game ticks between logistics rounds while the cell needs nothing: the rest of the factory, its power included, keeps running.</summary>
    public const long ServiceIntervalTicks = 3600;
    /// <summary>
    /// Observations, sixty ticks apart at least, that a supply step or a faulty cell may show no change of the silo or the cell stock.
    /// Three logistics rounds fit in them, enough for structure cells to deliver at the slowest planned rate.
    /// </summary>
    public const int StallObservations = 180;
    /// <summary>The caller's buffer logistics fills unplanned chests with; planned ones keep ten minutes of their share.</summary>
    private const int BufferCrafts = 40;

    private long? serviced;
    private string unhealed = "";
    private string? progress;
    private int stalled;

    /// <summary>
    /// One observation of the cell during a launch; true when no role is destroyed or unpowered. Logistics runs at the first sight of a
    /// fault, when an ingredient falls below its low-water mark and the actor carries it or must craft it, on a fixed cadence, and after
    /// a stall. Procurement follows that round, so collected structures count before anything is crafted, and only covers ingredients no
    /// ready cell makes. A supply step, or a faulty cell, that shows no progress over the stall budget gets one more maintenance round,
    /// then stops with a reconcile error naming what blocks it instead of waiting out the launch budget.
    /// </summary>
    public async Task<bool> TendAsync(string cellId, RocketSiloPrototype prototype, NativeRecipe recipe, ProductionCatalog catalog,
        Func<Task<ObservedRocketSilo>> readSilo, CancellationToken token)
    {
        var before = await ReadAsync(cellId, prototype, recipe, catalog.Scope, readSilo, token);
        stalled = (before.Step.Kind == "supply" || before.Faults.Count > 0) && before.Progress == progress ? stalled + 1 : 0;
        progress = before.Progress;
        if (before.Faults.Count == 0) unhealed = "";
        int buffer = FactoryLogistics.CellBufferCrafts(before.Cell, FactoryLogistics.CellShares(catalog, before.State), BufferCrafts);
        var carried = FactoryLogistics.Carried(before.Snapshot);
        bool restock = before.Faults.Count == 0 && SiloCellPlanner.Procurement(before.Step, recipe, before.Stock, buffer)
            .Any(p => p.Value > 0 && (carried.GetValueOrDefault(p.Key) > 0 || !MadeByCells(before.State, catalog, p.Key)));
        bool due = serviced is not { } last || before.Snapshot.CollectedTick - last >= ServiceIntervalTicks || restock
            || before.Faults.Count > 0 && before.FaultKey != unhealed || stalled >= StallObservations;
        if (!due) return before.Faults.Count == 0;

        var round = await new FactoryLogistics(game, journal, directory).ServiceAsync(BufferCrafts, token);
        serviced = round.Tick;
        var after = await ReadAsync(cellId, prototype, recipe, catalog.Scope, readSilo, token);
        // Faults maintenance could not heal are not worth another immediate round; a new or returning one is.
        unhealed = after.FaultKey;
        if (stalled >= StallObservations)
        {
            if (after.Progress == before.Progress && after.FaultKey == before.FaultKey)
            {
                await journal.AppendAsync("silo-cell-stalled", new { cell = after.Cell.Id, after.Faults, after.Step, after.Stock, observations = stalled, round.Tick }, token);
                throw new InvalidOperationException($"The silo cell {after.Cell.Id} made no progress over {stalled} observations, even after a maintenance round: " +
                    (after.Faults.Count > 0 ? string.Join(", ", after.Faults)
                        : $"no role is destroyed or unpowered, the cell holds [{Join(after.Stock)}] for [{Join(after.Step.RequiredItems.ToDictionary(p => p.Key, p => (long)p.Value))}]")
                    + ". Reconcile before continuing.");
            }
            stalled = 0;
        }
        // A destroyed or unpowered cell takes nothing in: procurement waits for maintenance.
        var wanted = after.Faults.Count > 0 ? new Dictionary<string, int>() : SiloCellPlanner.Procurement(after.Step, recipe, after.Stock, buffer);
        var bag = FactoryLogistics.Carried(after.Snapshot);
        var leftToCells = wanted.Where(p => p.Value > 0 && MadeByCells(after.State, catalog, p.Key)).Select(p => p.Key).ToArray();
        var craft = wanted.Where(p => p.Value > bag.GetValueOrDefault(p.Key) && !leftToCells.Contains(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        // The round and the procurement it leaves are journaled before production, which may run long or refuse the item.
        await journal.AppendAsync("silo-cell-supply", new
        {
            cell = after.Cell.Id, step = after.Step, stock = after.Stock, after.Faults, buffer, wanted, craft = craft.Select(p => p.Key),
            leftToCells, round.Supplied, round.Shortfall, round.Tick
        }, token);
        // The registry is reread: a rebuilt chest or inserter stays reserved against nested production under its new id.
        using (ProductionReservations.EnterFactory(after.State))
            foreach (var (item, count) in craft)
                await new ProductionGoalExecutor(game, journal).RunAsync(item, count, token);
        return after.Faults.Count == 0;
    }

    /// <summary>
    /// The factory's silo cell made launchable again when no silo stands for the launch, or null when the factory has none and may
    /// build one. A building cell is resumed; a ready cell whose silo is gone gets every destroyed role, silo included, rebuilt at its
    /// plan by maintenance from carried items. A factory never registers a second silo cell: anything else is reconciled first.
    /// </summary>
    public async Task<FactoryCell?> RestoreAsync(FactoryState state, string siloItem, RocketSiloPrototype prototype, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        var owned = Owned(state, siloItem);
        if (owned is null) return null;
        if (owned.Status == "building")
        {
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (known.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while resuming the silo cell.");
            // A placed silo already draws on the network; only an unplaced one still needs its power budgeted.
            if (owned.Entities.GetValueOrDefault("machine") is not { } machine || !FactoryMaintenance.Present(known).Contains(machine))
                await new PowerExpansionController(game, journal, directory).EnsureCapacityForCellsAsync(siloItem, 1, true, token);
            return await new FactoryCellBuilder(game, journal, directory).BuildAsync(SiloCellPlanner.Kind, siloItem, prototype.Recipe, token);
        }
        if (owned.Status != "ready" || owned.Plan?.ContainsKey("machine") != true)
            throw new InvalidOperationException($"The silo cell {owned.Id} is {owned.Status} without a planned silo; reconcile it before launching, a factory builds no second silo cell.");
        var registry = new FactoryRegistry(directory);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while restoring the silo cell; reconcile partial effects.");
        var missing = FactoryMaintenance.Degraded([owned], FactoryMaintenance.Present(snapshot)).SelectMany(d => d.Missing).ToArray();
        if (missing.Any(m => m.Item is null))
            throw new InvalidOperationException($"The silo cell {owned.Id} lost a role it has no plan for; reconcile it before launching.");
        var builder = new FactoryCellBuilder(game, journal, directory);
        foreach (var group in missing.GroupBy(m => m.Item!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            await builder.EnsureCarriedAsync(registry, catalog, group.Key, group.Count(), token);
        var upkeep = await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token);
        var restored = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var rockets = RocketSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("rocket_state"), token));
        if (rockets.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while restoring the silo cell; reconcile partial effects.");
        var cell = Registered(restored, rockets, prototype);
        await journal.AppendAsync("silo-cell-restored", new { owned.Id, missing, upkeep.Rebuilt, upkeep.Blocked, upkeep.Shortfall,
            silo = cell?.Entities["machine"], rockets.CollectedTick }, token);
        return cell?.Id == owned.Id ? cell : throw new InvalidOperationException(
            $"Maintenance did not restore the silo cell {owned.Id} at its plan (blocked: {string.Join(", ", upkeep.Blocked)}; short: {Join(upkeep.Shortfall)}); reconcile before launching.");
    }

    /// <summary>Ingredients already in the cell: its input chest and the input inserter's hand. The silo's own inputs are the planner's.</summary>
    internal static Dictionary<string, long> CellStock(FactorySnapshot snapshot, FactoryCell cell)
    {
        var stock = FactoryLogistics.Items(snapshot, cell.Entities["input-chest"]);
        foreach (var hand in snapshot.Records.Where(r => r.Kind == "transit" && r.EntityId == cell.Entities["input-inserter"]))
            foreach (var item in hand.Data.GetProperty("items").EnumerateObject())
                stock[item.Name] = stock.GetValueOrDefault(item.Name) + item.Value.GetInt64();
        return stock;
    }

    /// <summary>Roles that stop the cell: destroyed entities, and electric ones whose network holds no generator.</summary>
    internal static IReadOnlyList<string> Faults(FactorySnapshot snapshot, FactoryCell cell)
    {
        var unpowered = FactoryMaintenance.Unpowered(snapshot, cell.Entities.Values).ToHashSet(StringComparer.Ordinal);
        return FactoryMaintenance.Degraded([cell], FactoryMaintenance.Present(snapshot)).SelectMany(d => d.Missing).Select(m => $"{m.Role} missing")
            .Concat(cell.Entities.Where(e => unpowered.Contains(e.Value)).Select(e => $"{e.Key} unpowered")).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Whether a ready cell makes the item, so it waits for that cell's output, which logistics collects, instead of being crafted by
    /// hand: structures from their assemblers. Resource cells record their product as their recipe.
    /// </summary>
    internal static bool MadeByCells(FactoryState state, ProductionCatalog catalog, string item) => state.Cells.Any(c => c.Status == "ready"
        && c.Recipe is { } recipe && (catalog.Recipes.FirstOrDefault(r => r.Name == recipe)?.Products.Any(p => p.Name == item) ?? recipe == item));

    /// <summary>Silos of every registered silo cell, finished or not: none of them is ever fed by hand.</summary>
    internal static IReadOnlySet<string> Silos(FactoryState? state) => (state?.Cells ?? []).Where(c => c.Kind == SiloCellPlanner.Kind)
        .Select(c => c.Entities.GetValueOrDefault("machine")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>The factory's silo cell for this silo item, whatever its status or the state of its silo: ready first.</summary>
    internal static FactoryCell? Owned(FactoryState state, string siloItem) => state.Cells
        .Where(c => c.Kind == SiloCellPlanner.Kind && c.MachineItem == siloItem)
        .OrderByDescending(c => c.Status == "ready").ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The ready silo cell to launch from, its silo observed with the prototype: a ready rocket first, then the most parts. A cell
    /// whose silo is gone leaves the launch to loose silos, or else to <see cref="RestoreAsync"/>.
    /// </summary>
    internal static FactoryCell? Registered(FactoryState state, RocketSnapshot rockets, RocketSiloPrototype prototype)
    {
        var silos = rockets.Silos.Where(s => s.Name == prototype.EntityName).ToDictionary(s => s.Id, StringComparer.Ordinal);
        return state.Cells.Where(c => c.Kind == SiloCellPlanner.Kind && c.Status == "ready" && c.Entities.TryGetValue("machine", out var id)
                && silos.ContainsKey(id) && c.Entities.ContainsKey("input-chest") && c.Entities.ContainsKey("input-inserter"))
            .OrderByDescending(c => silos[c.Entities["machine"]].Status == "rocket_ready").ThenByDescending(c => silos[c.Entities["machine"]].Parts)
            .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The cell, its photograph and stock, then the silo read after that stock: an ingredient dropped meanwhile counts as loaded.</summary>
    private async Task<CellReading> ReadAsync(string cellId, RocketSiloPrototype prototype, NativeRecipe recipe, ActorScope scope,
        Func<Task<ObservedRocketSilo>> readSilo, CancellationToken token)
    {
        var state = await new FactoryRegistry(directory).LoadAsync(scope.WorldId, token);
        // Reread each time, so a chest or inserter that maintenance rebuilt counts under its new id.
        var cell = state.Cells.SingleOrDefault(c => c.Id == cellId)
            ?? throw new InvalidDataException($"The silo cell {cellId} left the factory registry; reconcile before supplying it.");
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != scope) throw new InvalidDataException("Actor changed during silo cell supply; reconcile partial effects.");
        var stock = CellStock(snapshot, cell);
        var silo = await readSilo();
        return new(state, cell, snapshot, Faults(snapshot, cell), stock, RocketPlanner.Next(prototype, silo, recipe),
            $"{silo.Status}|{silo.Parts}|{silo.InProcess}|{Join(silo.Inputs)}|{Join(stock)}");
    }

    private static string Join(IReadOnlyDictionary<string, long> items) => string.Join(", ", items.Where(p => p.Value != 0)
        .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}"));

    /// <summary>Progress concatenates what changes while the cell works: the silo's phase, parts, craft and inputs, and the cell stock.</summary>
    private sealed record CellReading(FactoryState State, FactoryCell Cell, FactorySnapshot Snapshot, IReadOnlyList<string> Faults,
        Dictionary<string, long> Stock, RocketStep Step, string Progress)
    {
        public string FaultKey => string.Join(", ", Faults);
    }
}
