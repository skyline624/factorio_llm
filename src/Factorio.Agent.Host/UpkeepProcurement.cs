using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Produces the parts a maintenance round could not rebuild for want of items, so the next round rebuilds them. Until 2026-10-08
/// only research goals procured such shortfalls; on that day (seed 20261107) biters had destroyed 22 poles, 11 chests and 9 arms
/// of a factory whose strategic goals were all military, and it stood at 0 of 89 working machines between goals.
/// </summary>
internal sealed class UpkeepProcurement(IGameClient game, IControllerJournal journal, string directory)
{
    /// <summary>Items procured in one round, most missing first.</summary>
    internal const int MaximumItems = 6;
    /// <summary>Units of one item procured in one round.</summary>
    internal const int MaximumUnits = 50;
    internal static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>The bounded procurement order: the items most missing first, each capped.</summary>
    internal static IReadOnlyList<(string Item, int Units)> Plan(IReadOnlyDictionary<string, long> shortfall) => shortfall
        .Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(MaximumItems)
        .Select(p => (p.Key, (int)Math.Min(p.Value, MaximumUnits))).ToArray();

    public async Task<int> RunAsync(IReadOnlyDictionary<string, long> shortfall, CancellationToken token)
    {
        var plan = Plan(shortfall);
        if (plan.Count == 0) return 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Budget);
        var executor = new ProductionGoalExecutor(game, journal);
        int procured = 0;
        foreach (var (item, units) in plan)
        {
            var carried = FactoryLogistics.Carried(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: deadline.Token));
            int target = (int)Math.Min(1000, carried.GetValueOrDefault(item) + units);
            StockGoalResult result;
            try
            {
                var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), deadline.Token));
                using (ProductionReservations.EnterFactory(await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, deadline.Token)))
                    result = await executor.RunAsync(item, target, deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await journal.AppendAsync("upkeep-procurement-budget-spent", new { item, units, budgetMinutes = Budget.TotalMinutes }, token);
                break;
            }
            await journal.AppendAsync("upkeep-procurement", new { item, units, result.InitialStock, result.FinalStock }, token);
            procured += (int)Math.Max(0, result.FinalStock - result.InitialStock);
        }
        return procured;
    }
}
