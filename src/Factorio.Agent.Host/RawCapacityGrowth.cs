namespace Factorio.Agent.Host;

/// <summary>
/// Decides when a recurring raw shortfall justifies one more resource cell. Shortfall rounds accumulate until growth,
/// growth is bounded per item, and an item whose growth failed is left to ordinary procurement for the rest of the run.
/// </summary>
public sealed class RawCapacityGrowth(int persistentRounds = 2, int maximumCells = 8)
{
    /// <summary>Rate requested for a raw item that no automation plan sized, such as fuel.</summary>
    public const double DefaultPerMinute = 15;
    private readonly Dictionary<string, int> rounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> added = new(StringComparer.Ordinal);
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);

    public void Observe(IEnumerable<string> shortfall)
    {
        foreach (string item in shortfall.Distinct(StringComparer.Ordinal)) rounds[item] = rounds.GetValueOrDefault(item) + 1;
    }

    public bool Due(string item) => !failed.Contains(item) && added.GetValueOrDefault(item) < maximumCells
        && rounds.GetValueOrDefault(item) >= persistentRounds;

    public void Grew(string item)
    {
        added[item] = added.GetValueOrDefault(item) + 1;
        rounds[item] = 0; // The new cell needs time to deliver before another shortfall counts.
    }

    public void Failed(string item) => failed.Add(item);

    /// <summary>The planned rate, or double the observed capacity once that rate has proven insufficient.</summary>
    public static double TargetRate(double planned, double current) => Math.Max(planned > 0 ? planned : DefaultPerMinute, 2 * current);
}
