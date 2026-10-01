using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Reads the force's map for the wanted resources before a blind exploration step, as a player looks at the map before walking.
/// The session client records every reading in the resource memory as charted destinations, which the existing historical
/// destination path then follows; arrival and local observation still decide every site. On 2026-10-01 (seed 20261002) the
/// crude-oil search explored blind frontiers and the actor died about fifteen times in biter territory.
/// </summary>
internal sealed class ChartedResourceSurvey(IGameClient game, IControllerJournal journal)
{
    /// <summary>Exploration steps between two readings of one search: the actor's own charted square grows as it walks.</summary>
    public const int Cadence = 8;
    private readonly Dictionary<string, int> steps = new(StringComparer.Ordinal);

    public Task<ChartedResourceSnapshot?> BeforeExplorationAsync(ProductionCatalog catalog, string product, string purpose, CancellationToken token) =>
        BeforeExplorationAsync(Sources(catalog, product), purpose, token);

    /// <summary>Reads the map on a search's first exploration step and every <see cref="Cadence"/> steps after it.</summary>
    public async Task<ChartedResourceSnapshot?> BeforeExplorationAsync(IReadOnlyList<string> names, string purpose, CancellationToken token)
    {
        if (names.Count == 0) return null;
        string key = string.Join('\n', names);
        int step = steps.GetValueOrDefault(key);
        steps[key] = step + 1;
        return step % Cadence == 0 ? await ReadAsync(names, purpose, token) : null;
    }

    public async Task<ChartedResourceSnapshot?> ReadAsync(IReadOnlyList<string> names, string purpose, CancellationToken token)
    {
        ChartedResourceSnapshot charted;
        try { charted = await new ChartedResourceClient(game).CaptureAsync(names, cancellationToken: token); }
        catch (GameRpcException error) when (error.Error.Code == "unknown_action")
        {
            // A world still running an older mod cannot read its map; the search goes on as before.
            await journal.AppendAsync("charted-resource-survey-unavailable", new { purpose, names, error.Error.Code }, token);
            return null;
        }
        await journal.AppendAsync("charted-resource-survey", new
        {
            purpose,
            charted.Scope,
            charted.CollectedTick,
            charted.SurfaceIndex,
            charted.Center,
            charted.Radius,
            charted.Names,
            charted.Coverage,
            deposits = charted.Deposits.Count,
            nearest = charted.Deposits.Take(8),
            interpretation = "charted-destination-requires-local-reobservation"
        }, token);
        return charted;
    }

    /// <summary>Native resources, not trees, whose mining yields the product; at most the eight names one reading takes.</summary>
    public static IReadOnlyList<string> Sources(ProductionCatalog catalog, string product) => catalog.Mining
        .Where(p => catalog.MiningSourceTypes?.GetValueOrDefault(p.Key) == "resource"
            && p.Value.Any(m => m.Name == product && (m.DeterministicItem || m.DeterministicFluid)))
        .Select(p => p.Key).Order(StringComparer.Ordinal).Take(ChartedResourceSnapshot.MaximumNames).ToArray();
}
