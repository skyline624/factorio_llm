namespace Factorio.Agent.Host;

/// <summary>
/// Decides when a raw shortfall justifies one more resource cell. The shortfall must persist over consecutive logistics
/// rounds that span real game time, so a new cell delivers before the next one counts; ready cells must fall short of
/// the demanded rate; and each item's cells stay within a budget counted in the registry across research runs. An item
/// whose growth failed is left to ordinary procurement until a retry delay passes: depleted local deposits leave no row
/// site in view, but a later attempt that explores toward remembered deposits may succeed.
/// </summary>
public sealed class RawCapacityGrowth(long persistentTicks = 3600, int persistentRounds = 2, int maximumCells = 16)
{
    /// <summary>Game ticks (ten minutes) a failed growth waits before the item may grow again.</summary>
    public const long RetryTicks = 36000;
    /// <summary>Exploration steps toward remembered deposits when no deposit in view holds a resource row.</summary>
    public const int ExplorationBudget = 6;
    /// <summary>Rate requested for a raw item that no automation plan sized, such as fuel.</summary>
    public const double DefaultPerMinute = 15;
    /// <summary>Share of their native rate that ready cells must deliver before an unplanned item grows past it.</summary>
    public const double SaturatedShare = .8;
    private sealed record Streak(long FirstTick, long LastTick, int Rounds, long Delivered);
    private readonly Dictionary<string, Streak> streaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> procured = new(StringComparer.Ordinal);

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
    /// Rate the ready cells must reach: the planned rate, or the default rate for an unplanned item such as fuel; one
    /// default step beyond capacity once ready cells deliver their capacity and the item still runs short. Plans leave out
    /// chest buffers and unplanned consumers such as ammunition, so saturated cells may fall short of a covered plan.
    /// </summary>
    public double Demand(string item, double planned, double capacity)
    {
        double baseline = planned > 0 ? planned : DefaultPerMinute;
        bool beyondCells = Delivered(item) >= SaturatedShare * capacity || procured.Contains(item);
        return capacity > 0 && beyondCells ? Math.Max(baseline, capacity + DefaultPerMinute) : baseline;
    }

    /// <summary>
    /// The actor had to procure the item: its ready cells, saturated, idle or starved alike, did not cover the factory. On
    /// 2026-10-01 (seed 20261002) the only coal miner ran out of its own coal, delivered nothing and never looked saturated.
    /// </summary>
    public void Procured(string item) => procured.Add(item);

    public bool Due(string item, int cells, double capacity, double demand) => cells < maximumCells
        && capacity < demand - 1e-9 && streaks.TryGetValue(item, out var streak) && !HasFailed(item, streak.LastTick)
        && streak.Rounds >= persistentRounds && streak.LastTick - streak.FirstTick >= persistentTicks;

    public void Grew(string item)
    {
        // The new cell must deliver before another shortfall or procurement counts.
        streaks.Remove(item);
        procured.Remove(item);
    }

    public void Failed(string item, long tick) => failed[item] = tick;

    public bool HasFailed(string item, long tick) => failed.TryGetValue(item, out long at) && tick < at + RetryTicks;

    /// <summary>Resource cells of the item that stand or are being built; depleted and abandoned cells leave the budget.</summary>
    public static int Cells(FactoryState state, string item) =>
        state.Cells.Count(c => c.IsResource && c.Recipe == item && c.Status is "ready" or "building");
}
