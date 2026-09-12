using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record StockGoalResult(string Method, string Item, int TargetStock, long InitialStock, long FinalStock,
    long StartTick, long EndTick);

public sealed class ProductionGoalExecutor(IGameClient game, IControllerJournal journal)
{
    public async Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        var production = new ProductionController(game, journal);
        ProductionState initial = await production.ObserveAsync(token);
        if (initial.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        long stock = initial.Inventory.GetValueOrDefault(item);
        StockGoalResult result;
        if (stock >= targetStock)
            result = new("already-satisfied", item, targetStock, stock, stock, initial.Tick, initial.Tick);
        else
        {
            var automated = new AutomatedSmeltingController(game, journal);
            SmeltingPlan? opportunity = await automated.AssessAsync(item, initial, token);
            string method = opportunity is null ? "actor-production" : "automated-smelting";
            await journal.AppendAsync("production-method", new { method, item, targetStock, initial.Scope, initial.Tick, stock, opportunity }, token);
            // Both executors recollect state before acting. A failed execution never triggers
            // an alternate mutation path: partial or unknown effects require reconciliation.
            if (opportunity is not null)
            {
                AutomatedSmeltingResult completed = await automated.RunAsync(item, targetStock, token);
                result = new(method, item, targetStock, completed.InitialStock, completed.FinalStock, completed.StartTick, completed.EndTick);
            }
            else
            {
                ProductionResult completed = await production.ProduceAsync(item, targetStock, token);
                result = new(method, item, targetStock, completed.InitialStock, completed.FinalStock, completed.StartTick, completed.EndTick);
            }
        }
        await journal.AppendAsync("stock-goal-result", result, token);
        return result;
    }
}
