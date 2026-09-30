namespace Factorio.Agent.Host;

/// <summary>
/// Remembers when cells last delivered each item, counting a newly ready resource cell as a delivery. Cells that fall
/// silent (unpowered, unfuelled, depleted or destroyed) stop covering their product after a window of game time.
/// </summary>
public sealed class CellDelivery(long windowTicks = 3600)
{
    private readonly Dictionary<string, long> last = new(StringComparer.Ordinal);

    public void Observe(LogisticsResult round, IEnumerable<FactoryCell> cells)
    {
        foreach (var (item, count) in round.Collected)
            if (count > 0) Seen(item, round.Tick);
        foreach (var cell in cells.Where(c => c.Zone == 0 && c.Status == "ready" && c.Recipe is not null)) Seen(cell.Recipe!, cell.Tick);
    }

    public bool Covers(string item, long tick) => last.TryGetValue(item, out long seen) && tick - seen <= windowTicks;

    private void Seen(string item, long tick) => last[item] = Math.Max(last.GetValueOrDefault(item, long.MinValue), tick);
}
