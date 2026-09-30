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

    public static async Task EnsureAsync(IStockGoalExecutor executor, IReadOnlyDictionary<string, long> carried, string item, int count,
        CancellationToken token)
    {
        if (count > 0 && carried.GetValueOrDefault(item) < count) await executor.RunAsync(item, Math.Min(1000, count), token);
    }
}
