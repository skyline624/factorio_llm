using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Feeds a registered silo cell toward a launch. Only the cell's inserter moves ingredients into the silo: the actor procures what
/// the cell's chest, its inserter's hand and the bag lack for the silo's next bounded batch, then factory logistics restocks the
/// chest, as it restocks every planned cell.
/// </summary>
internal sealed class SiloCellSupply(IGameClient game, IControllerJournal journal, string directory)
{
    /// <summary>
    /// One supply round; null when the cell already holds the batch and its inserter only needs time. The cell is reread so a chest
    /// or inserter that maintenance rebuilt counts under its new id. The silo's step is read after the cell's stock, so an ingredient
    /// the inserter drops meanwhile counts as loaded instead of missing and is never procured twice.
    /// </summary>
    public async Task<LogisticsResult?> FeedAsync(string cellId, NativeRecipe recipe, ActorScope scope, Func<Task<RocketStep>> readStep,
        CancellationToken token)
    {
        var cell = (await new FactoryRegistry(directory).LoadAsync(scope.WorldId, token)).Cells.SingleOrDefault(c => c.Id == cellId)
            ?? throw new InvalidDataException($"The silo cell {cellId} left the factory registry; reconcile before supplying it.");
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != scope) throw new InvalidDataException("Actor changed during silo cell supply; reconcile partial effects.");
        var stock = CellStock(snapshot, cell);
        var step = await readStep();
        var wanted = SiloCellPlanner.Procurement(step, recipe, stock);
        if (wanted.Values.All(count => count == 0)) return null;
        var carried = FactoryLogistics.Carried(snapshot);
        foreach (var (item, count) in wanted.Where(p => p.Value > carried.GetValueOrDefault(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            await new ProductionGoalExecutor(game, journal).RunAsync(item, count, token);
        var round = await new FactoryLogistics(game, journal, directory).ServiceAsync(token: token);
        await journal.AppendAsync("silo-cell-supply", new { cell.Id, step, stock, wanted, round.Supplied, round.Shortfall, round.Tick }, token);
        return round;
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

    /// <summary>Silos of every registered silo cell, finished or not: none of them is ever fed by hand.</summary>
    internal static IReadOnlySet<string> Silos(FactoryState? state) => (state?.Cells ?? []).Where(c => c.Kind == SiloCellPlanner.Kind)
        .Select(c => c.Entities.GetValueOrDefault("machine")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The ready silo cell to launch from, its silo observed with the prototype: a ready rocket first, then the most parts. A cell
    /// whose silo is gone leaves the launch to other silos until maintenance rebuilds it.
    /// </summary>
    internal static FactoryCell? Registered(FactoryState state, RocketSnapshot rockets, RocketSiloPrototype prototype)
    {
        var silos = rockets.Silos.Where(s => s.Name == prototype.EntityName).ToDictionary(s => s.Id, StringComparer.Ordinal);
        return state.Cells.Where(c => c.Kind == SiloCellPlanner.Kind && c.Status == "ready" && c.Entities.TryGetValue("machine", out var id)
                && silos.ContainsKey(id) && c.Entities.ContainsKey("input-chest") && c.Entities.ContainsKey("input-inserter"))
            .OrderByDescending(c => silos[c.Entities["machine"]].Status == "rocket_ready").ThenByDescending(c => silos[c.Entities["machine"]].Parts)
            .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
    }
}
