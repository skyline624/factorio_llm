using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record StockGoalResult(string Method, string Item, int TargetStock, long InitialStock, long FinalStock,
    long StartTick, long EndTick);

public interface IStockGoalExecutor
{
    Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default);
}

public sealed class ProductionGoalExecutor(IGameClient game, IControllerJournal journal) : IStockGoalExecutor
{
    public async Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        var production = new ProductionController(game, journal);
        ProductionState initial = await production.ObserveAsync(token);
        if (initial.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        var baseline = initial;
        // Finished stock comes before installing or restarting a production connection. A partial collection
        // still leaves the remaining amount to the same capability assessment, rather than manual procurement.
        if (initial.Inventory.GetValueOrDefault(item) < targetStock && initial.AvailableOutput(item) is not null)
        {
            await production.CollectAvailableAsync(item, targetStock, token, expectedScope: baseline.Scope);
            initial = await production.ObserveAsync(token);
            if (initial.Scope != baseline.Scope || initial.ControlMode != "ai")
                throw new InvalidDataException("Actor changed during stock collection; reconcile before choosing production.");
        }
        long stock = initial.Inventory.GetValueOrDefault(item);
        StockGoalResult result;
        if (stock >= targetStock)
            result = new(baseline.Inventory.GetValueOrDefault(item) >= targetStock ? "already-satisfied" : "collected-output",
                item, targetStock, stock, stock, initial.Tick, initial.Tick);
        else
        {
            var automated = new AutomatedSmeltingController(game, journal);
            SmeltingPlan? opportunity = await automated.AssessAsync(item, initial, token);
            string method = opportunity is null ? "planned-production" : "automated-smelting";
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
        result = result with { InitialStock = baseline.Inventory.GetValueOrDefault(item), StartTick = baseline.Tick };
        await journal.AppendAsync("stock-goal-result", result, token);
        return result;
    }
}
