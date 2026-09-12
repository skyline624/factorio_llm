namespace Factorio.Agent.Host;

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
        finally
        {
            await deadline.CancelAsync();
            try { await planning; } catch (OperationCanceledException) { }
        }
    }
}
