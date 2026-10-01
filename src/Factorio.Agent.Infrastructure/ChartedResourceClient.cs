using Factorio.Agent.Core;

namespace Factorio.Agent.Infrastructure;

/// <summary>Reads resource patches from the force's map; the reply must answer exactly the request.</summary>
public sealed class ChartedResourceClient(IGameClient game)
{
    /// <summary>Covers the 14-chunk long-range scan of a base radar placed near the actor.</summary>
    public const int DefaultRadius = 512, DefaultLimit = 64;

    public async Task<ChartedResourceSnapshot> CaptureAsync(IReadOnlyList<string> names, int radius = DefaultRadius, MapPosition? center = null,
        int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        if (names is null || names.Count is < 1 or > ChartedResourceSnapshot.MaximumNames || names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException("Supply one to eight distinct resource names.", nameof(names));
        if (radius is < ChartedResourceSnapshot.MinimumRadius or > ChartedResourceSnapshot.MaximumRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (limit is < ChartedResourceSnapshot.MaximumNames or > ChartedResourceSnapshot.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (center is not null && (!double.IsFinite(center.X) || !double.IsFinite(center.Y)))
            throw new ArgumentOutOfRangeException(nameof(center));
        GameResponse response = await game.ExecuteAsync(GameRequest.Create("charted_resources",
            center is null ? new { names, radius, limit } : new { names, radius, limit, center }), cancellationToken);
        ChartedResourceSnapshot charted = ChartedResourceSnapshot.Parse(response);
        if (!charted.Names.SequenceEqual(names, StringComparer.Ordinal) || charted.Radius != radius || charted.Limit != limit
            || center is not null && charted.Center.DistanceTo(center) > 1e-6)
            throw new InvalidDataException("The charted resource reading does not answer its request.");
        return charted;
    }
}
