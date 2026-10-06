namespace Factorio.Agent.Host;

/// <summary>A bounded pure search ran out of its own time budget; the caller's token was not cancelled.</summary>
internal sealed class PlanningBudgetExceededException(TimeSpan budget)
    : TimeoutException($"Planning exceeded its {budget.TotalSeconds:0} s budget.");

/// <summary>Keeps the native defense arbitration active during bounded pure C# searches.</summary>
internal static class ControllerPlanning
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, T> search, SpatialController controller,
        TimeSpan budget, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        var planning = Task.Run(() => search(deadline.Token), deadline.Token);
        try
        {
            while (!planning.IsCompleted)
            {
                var waited = await controller.WorkAsync("wait", new { ticks = 30 }, 600, token: token);
                if (waited.Status != "completed") throw new InvalidOperationException("Planning supervision did not complete its native wait.");
            }
            return await planning;
        }
        // On 2026-10-06 (seed 20261106) a balanced-bus search that outran its 45 s budget surfaced as a bare cancellation and
        // failed a 42-minute factory goal; the budget is the caller's to judge, so it gets a timeout it can recognise.
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new PlanningBudgetExceededException(budget);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await planning; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>For searches whose null result already means "no plan": an exhausted budget is that same answer.</summary>
    public static async Task<T?> TryRunAsync<T>(Func<CancellationToken, T?> search, SpatialController controller,
        TimeSpan budget, CancellationToken token) where T : class
    {
        try { return await RunAsync(search, controller, budget, token); }
        catch (PlanningBudgetExceededException) { return null; }
    }
}
