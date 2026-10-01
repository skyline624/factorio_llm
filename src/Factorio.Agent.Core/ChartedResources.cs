using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed record ChartedChunk(int X, int Y)
{
    public const int Size = 32;
    public static ChartedChunk Of(MapPosition point) => new(checked((int)Math.Floor(point.X / Size)), checked((int)Math.Floor(point.Y / Size)));

    /// <summary>Distance from a point to this chunk's square, zero inside it; the mod orders and bounds chunks by it.</summary>
    public double DistanceTo(MapPosition point)
    {
        double dx = Math.Max(Math.Max(X * Size - point.X, 0), point.X - (X + 1) * Size);
        double dy = Math.Max(Math.Max(Y * Size - point.Y, 0), point.Y - (Y + 1) * Size);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>An actual resource entity of the chunk, the one nearest the reading's center.</summary>
public sealed record ChartedSample(string Id, MapPosition Position);

/// <summary>One resource name in one chunk: its sample entity, entity count and summed native amount.</summary>
public sealed record ChartedDeposit(string Name, ChartedChunk Chunk, bool Charted, ChartedSample Sample, int Count, double Amount);

/// <summary>
/// Chunks within the radius, those read because the engine charted them or the force requested their normal charting, the
/// resource entities aggregated, and whether the reading stopped at its deposit limit. When truncated, every point nearer than
/// <see cref="CompleteRadius"/> lies in a chunk read in full.
/// </summary>
public sealed record ChartedCoverage(string Visibility, int ConsideredChunks, int ChartedChunks, int RequestedChunks, int Entities,
    bool Truncated, double CompleteRadius)
{
    [JsonIgnore] public int ReadChunks => ChartedChunks + RequestedChunks;
}

/// <summary>
/// Resource patches read from the force's map, as a player reads the map: chunks the engine charted, or whose normal charting
/// (the character's square, a radar sector) the force requested and the engine leaves pending while no player is connected.
/// Only resource entities of the requested names are read: never enemies. It is a destination hint, not proof of a buildable,
/// safe or current site; local observation decides.
/// </summary>
public sealed record ChartedResourceSnapshot(ActorScope Scope, long CollectedTick, int SurfaceIndex, MapPosition Center, int Radius,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Names, int Limit,
    [property: JsonConverter(typeof(NativeArrayConverter<ChartedDeposit>))] IReadOnlyList<ChartedDeposit> Deposits,
    ChartedCoverage Coverage)
{
    public const int MaximumNames = 8, MinimumRadius = 32, MaximumRadius = 1024, MaximumLimit = 256;
    public const string MapVisibility = "force-charted-or-requested-chunks";
    /// <summary>Chunks a square of the maximum radius can touch whatever its center.</summary>
    public const int MaximumChunks = (2 * MaximumRadius / ChartedChunk.Size + 2) * (2 * MaximumRadius / ChartedChunk.Size + 2);

    /// <summary>Whether the reading covered the whole chunk holding this point.</summary>
    public bool Covers(MapPosition point)
    {
        double distance = ChartedChunk.Of(point).DistanceTo(Center);
        return Coverage.Truncated ? distance < Coverage.CompleteRadius : distance <= Radius;
    }

    public static ChartedResourceSnapshot Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error ?? new("invalid_response", "Charted resource reading failed."));
        try
        {
            var charted = response.Data.Deserialize<ChartedResourceSnapshot>(Protocol.Json)
                ?? throw new InvalidDataException("Missing charted resource reading.");
            charted.Validate(response.Tick);
            return charted;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid native charted resource data.", error);
        }
    }

    private void Validate(long tick)
    {
        var coverage = Coverage;
        if (Scope is null || Center is null || Names is null || Deposits is null || coverage is null || CollectedTick != tick
            || SurfaceIndex < 1 || !double.IsFinite(Center.X) || !double.IsFinite(Center.Y)
            || Radius is < MinimumRadius or > MaximumRadius || Limit is < MaximumNames or > MaximumLimit
            || Names.Count is < 1 or > MaximumNames || Names.Any(string.IsNullOrWhiteSpace)
            || Names.Distinct(StringComparer.Ordinal).Count() != Names.Count
            || coverage.Visibility != MapVisibility || coverage.ConsideredChunks is < 1 or > MaximumChunks
            || coverage.ChartedChunks < 0 || coverage.RequestedChunks < 0 || coverage.ReadChunks > coverage.ConsideredChunks
            || !double.IsFinite(coverage.CompleteRadius) || coverage.CompleteRadius < 0 || coverage.CompleteRadius > Radius
            || !coverage.Truncated && coverage.CompleteRadius != Radius
            || Deposits.Count > Limit || Deposits.Any(d => d is null) || coverage.Entities != Deposits.Sum(d => (long)d.Count))
            throw new InvalidDataException("Inconsistent charted resource coverage.");
        var seen = new HashSet<(string, int, int)>();
        var flags = new Dictionary<ChartedChunk, bool>();
        double previous = 0;
        foreach (var deposit in Deposits)
        {
            if (deposit.Chunk is null || deposit.Sample?.Position is null || string.IsNullOrWhiteSpace(deposit.Sample.Id)
                || deposit.Sample.Id.Length > 256 || !Names.Contains(deposit.Name, StringComparer.Ordinal)
                || !seen.Add((deposit.Name, deposit.Chunk.X, deposit.Chunk.Y))
                || !double.IsFinite(deposit.Sample.Position.X) || !double.IsFinite(deposit.Sample.Position.Y)
                || ChartedChunk.Of(deposit.Sample.Position) != deposit.Chunk
                || deposit.Count is < 1 or > ChartedChunk.Size * ChartedChunk.Size
                || !double.IsFinite(deposit.Amount) || deposit.Amount <= 0
                || flags.TryGetValue(deposit.Chunk, out bool charted) && charted != deposit.Charted)
                throw new InvalidDataException("Invalid charted resource deposit.");
            flags[deposit.Chunk] = deposit.Charted;
            double distance = deposit.Chunk.DistanceTo(Center);
            if (distance > Radius || distance < previous || coverage.Truncated && distance >= coverage.CompleteRadius)
                throw new InvalidDataException("A charted deposit lies outside its reading or out of distance order.");
            previous = distance;
        }
        if (flags.Count > coverage.ReadChunks || flags.Count(f => f.Value) > coverage.ChartedChunks
            || flags.Count(f => !f.Value) > coverage.RequestedChunks)
            throw new InvalidDataException("Charted deposits lie in more chunks than the reading covered.");
    }
}
