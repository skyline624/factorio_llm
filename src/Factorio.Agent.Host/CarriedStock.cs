using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Tops up what the actor carries before it builds. Only the actor's inventory counts: entities already placed never
/// stand in for construction items still to be placed.
/// </summary>
internal static class CarriedStock
{
    /// <summary>Items of the layout parts that no recorded native entity stands for yet.</summary>
    public static IReadOnlyDictionary<string, int> Unplaced(CellLayout layout, IReadOnlyDictionary<string, string> recorded) => layout.Entities
        .Where(e => !recorded.ContainsKey(e.Role)).GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    public static async Task EnsureAsync(IGameClient game, IControllerJournal journal, string item, int count, CancellationToken token)
    {
        if (count <= 0) return;
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        await EnsureAsync(new ProductionGoalExecutor(game, journal), carried, item, count, token);
    }

    public static async Task EnsureAsync(IGameClient game, IControllerJournal journal, ProductionCatalog catalog,
        IReadOnlyDictionary<string, int> needed, CancellationToken token)
    {
        if (needed.Count == 0) return;
        var production = new ProductionController(game, journal);
        var initial = await production.ObserveAsync(token);
        Require(initial);
        var outputs = initial.Entities.Where(e => ProductionReservations.Collects(e.Id))
            .SelectMany(e => e.Items("output")).Where(p => p.Value > 0).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var plan = ConstructionSupplyPlanner.Plan(catalog, needed, initial.Inventory, outputs);
        await journal.AppendAsync("construction-supply-plan", new { initial.Scope, initial.Tick, needed, bundled = plan is not null, plan }, token);
        foreach (var target in plan?.Materials ?? []) await SupplyAsync(target);
        foreach (var target in plan?.Equipment ?? needed.Select(p => new ConstructionStock(p.Key, p.Value)).ToArray())
            await SupplyAsync(target);
        var final = await production.ObserveAsync(token);
        Require(final);
        if (needed.Any(p => final.Inventory.GetValueOrDefault(p.Key) < p.Value))
            throw new InvalidDataException("The construction kit is incomplete after procurement; reconcile before building.");
        await journal.AppendAsync("construction-supply-result", new { final.Scope, final.Tick,
            carried = needed.ToDictionary(p => p.Key, p => final.Inventory.GetValueOrDefault(p.Key), StringComparer.Ordinal) }, token);

        async Task SupplyAsync(ConstructionStock target)
        {
            var current = await production.ObserveAsync(token);
            Require(current);
            await EnsureAsync(new ProductionGoalExecutor(game, journal), current.Inventory, target.Item, target.TargetStock, token);
        }
        void Require(ProductionState state)
        {
            if (state.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while procuring the construction kit.");
            if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        }
    }

    public static async Task EnsureAsync(IStockGoalExecutor executor, IReadOnlyDictionary<string, long> carried, string item, int count,
        CancellationToken token)
    {
        if (count > 0 && carried.GetValueOrDefault(item) < count) await executor.RunAsync(item, Math.Min(1000, count), token);
    }
}
