namespace Factorio.Agent.Host;

/// <summary>
/// Decides when a raw shortfall justifies one more resource cell. The shortfall must persist over consecutive logistics
/// rounds that span real game time, so a new cell delivers before the next one counts; ready cells must fall short of
/// the demanded rate; and each item's cells stay within a budget counted in the registry across research runs. An item
/// whose growth failed is left to ordinary procurement for the rest of the run.
/// </summary>
public sealed class RawCapacityGrowth(long persistentTicks = 3600, int persistentRounds = 2, int maximumCells = 16)
{
    /// <summary>Rate requested for a raw item that no automation plan sized, such as fuel.</summary>
    public const double DefaultPerMinute = 15;
    /// <summary>Share of their native rate that ready cells must deliver before an unplanned item grows past it.</summary>
    public const double SaturatedShare = .8;
    private sealed record Streak(long FirstTick, long LastTick, int Rounds, long Delivered);
    private readonly Dictionary<string, Streak> streaks = new(StringComparer.Ordinal);
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);

    /// <summary>A short item extends its streak with what cells delivered since it began; any other item ends its streak.</summary>
    public void Observe(LogisticsResult round)
    {
        foreach (string item in streaks.Keys.Where(i => round.Shortfall.GetValueOrDefault(i) <= 0).ToArray()) streaks.Remove(item);
        foreach (string item in round.Shortfall.Where(p => p.Value > 0).Select(p => p.Key))
            // The first round collects what accumulated before the streak, which is not a rate over it.
            streaks[item] = streaks.TryGetValue(item, out var streak)
                ? streak with { LastTick = round.Tick, Rounds = streak.Rounds + 1, Delivered = streak.Delivered + round.Collected.GetValueOrDefault(item) }
                : new(round.Tick, round.Tick, 1, 0);
    }

    /// <summary>Items per minute that cells delivered over the current shortfall streak; zero before it spans time.</summary>
    public double Delivered(string item) => streaks.TryGetValue(item, out var streak) && streak.LastTick > streak.FirstTick
        ? streak.Delivered * 3600.0 / (streak.LastTick - streak.FirstTick) : 0;

    /// <summary>
    /// Rate the ready cells must reach: the planned rate; for an unplanned item such as fuel, the default rate, or one
    /// default step beyond capacity once ready cells deliver their capacity and the item still runs short.
    /// </summary>
    public double Demand(string item, double planned, double capacity)
    {
        if (planned > 0) return planned;
        return capacity > 0 && Delivered(item) >= SaturatedShare * capacity ? capacity + DefaultPerMinute : DefaultPerMinute;
    }

    public bool Due(string item, int cells, double capacity, double demand) => !failed.Contains(item) && cells < maximumCells
        && capacity < demand - 1e-9 && streaks.TryGetValue(item, out var streak) && streak.Rounds >= persistentRounds
        && streak.LastTick - streak.FirstTick >= persistentTicks;

    public void Grew(string item) => streaks.Remove(item); // The new cell must deliver before another shortfall counts.

    public void Failed(string item) => failed.Add(item);

    public bool HasFailed(string item) => failed.Contains(item);

    /// <summary>Resource cells of the item that stand or are being built; depleted and abandoned cells leave the budget.</summary>
    public static int Cells(FactoryState state, string item) =>
        state.Cells.Count(c => c.IsResource && c.Recipe == item && c.Status is "ready" or "building");
}
