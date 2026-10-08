using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record TransportLinkAttempt(string SourceCellId, string TargetCellId, string Item, long Tick);
public sealed record TransportLinkAttemptState(int Version, string WorldId, IReadOnlyList<TransportLinkAttempt> Attempts);

/// <summary>
/// Transport links tried without a bus to show for it. A link is recorded before it starts, so a search cancelled by the growth
/// budget counts too: on 2026-10-08 (seed 20261106) every between-goal round spent its twelve minutes surveying the same far
/// corridor and no belt had been built for eight hours. A recorded pair waits <see cref="RetryTicks"/> before another try.
/// </summary>
internal sealed class TransportLinkAttempts(string directory)
{
    /// <summary>One game hour.</summary>
    public const long RetryTicks = 216000;
    private string StatePath => Path.Combine(directory, "transport-link-attempts.json");

    public async Task<TransportLinkAttemptState> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(StatePath)) return new(1, worldId, []);
        var state = JsonSerializer.Deserialize<TransportLinkAttemptState>(await File.ReadAllTextAsync(StatePath, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty transport link attempt state.");
        if (state.Version != 1 || state.WorldId != worldId) throw new InvalidDataException("Transport link attempts belong to another world.");
        return state;
    }

    public async Task RecordAsync(string worldId, TransportLinkAttempt attempt, CancellationToken token)
    {
        var state = await LoadAsync(worldId, token);
        await LocalJson.WriteAsync(StatePath, With(state, attempt), token);
    }

    internal static TransportLinkAttemptState With(TransportLinkAttemptState state, TransportLinkAttempt attempt) => state with
    {
        Attempts = state.Attempts.Where(a => !Same(a, attempt.SourceCellId, attempt.TargetCellId, attempt.Item)).Append(attempt).TakeLast(256).ToArray()
    };

    /// <summary>Whether the pair was tried less than <see cref="RetryTicks"/> before the tick.</summary>
    internal static bool Delayed(TransportLinkAttemptState state, string sourceCellId, string targetCellId, string item, long tick) =>
        state.Attempts.Any(a => Same(a, sourceCellId, targetCellId, item) && tick - a.Tick is >= 0 and < RetryTicks);

    private static bool Same(TransportLinkAttempt attempt, string sourceCellId, string targetCellId, string item) =>
        attempt.SourceCellId == sourceCellId && attempt.TargetCellId == targetCellId && attempt.Item == item;
}
