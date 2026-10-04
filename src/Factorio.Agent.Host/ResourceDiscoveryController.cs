using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ResourceDiscoveryResult(string Resource, string EntityId, MapPosition Position, double NativeAmount,
    long StartTick, long EndTick, int SearchSteps);
public sealed record ResourceSearchProgressResult(string Resource, long StartTick, long EndTick, int SearchSteps,
    int NewSurveyedCells, bool CoverageTruncated);
public sealed record ResourceDiscoveryAttempt(ResourceDiscoveryResult? Discovery = null, ResourceSearchProgressResult? Progress = null);
public sealed class ResourceSearchBudgetExceededException(ResourceSearchProgressResult progress)
    : TimeoutException($"Resource discovery exhausted {progress.SearchSteps} local steps for {progress.Resource}; no deposit proved, global absence remains unknown.")
{
    public ResourceSearchProgressResult Progress { get; } = progress;
}

/// <summary>Locally observes a native deposit; remembered and charted positions guide travel but never prove discovery.</summary>
public sealed class ResourceDiscoveryController(IGameClient game, IControllerJournal journal, string? factoryDirectory = null)
{
    public const int MaximumSearchSteps = 64;

    public static bool Supported(ProductionCatalog catalog, string resource) =>
        catalog.MiningSourceTypes?.GetValueOrDefault(resource) == "resource" && catalog.Mining.ContainsKey(resource);

    public async Task<ResourceDiscoveryResult> RunAsync(string resource, CancellationToken token = default)
    {
        var attempt = await SearchAsync(resource, token);
        return attempt.Discovery ?? throw new ResourceSearchBudgetExceededException(attempt.Progress!);
    }

    public async Task<ResourceDiscoveryAttempt> SearchAsync(string resource, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (!Supported(catalog, resource)) throw new InvalidOperationException("Exploration requires an exact native resource identifier.");
        var spatial = new SpatialClient(game);
        var charting = new ChartedResourceSurvey(game, journal);
        var exploration = new ExplorationPlanner();
        bool radarConsulted = false;
        long startTick = catalog.CollectedTick;
        int? surface = null;
        HashSet<SurveyedCell>? previousCells = null;
        bool truncatedHistory = false;
        var observedCells = new HashSet<SurveyedCell>();
        SpatialSnapshot? lastMap = null;
        await using var controller = new SpatialController(game, journal);
        for (int steps = 0; steps <= MaximumSearchSteps; steps++)
        {
            var map = await CaptureAsync();
            var deaths = await DeathsAsync(map);
            if (Find(map, deaths) is { } deposit) return await CompleteAsync(deposit, map, steps);
            if (steps == MaximumSearchSteps) break;
            // The force's chart and memory supply hints only. Capture again after charting so history cannot be newer than the view.
            var charted = await charting.BeforeExplorationAsync([resource], "resource-discovery", token);
            if (charted is not null) map = await CaptureAsync();
            deaths = await DeathsAsync(map);
            if (Find(map, deaths) is { } newlyObserved) return await CompleteAsync(newlyObserved, map, steps);
            var destination = await DestinationAsync(map, deaths);
            if (destination is null && factoryDirectory is not null && !radarConsulted)
            {
                radarConsulted = true;
                var radars = new RadarController(game, journal, factoryDirectory);
                try
                {
                    if (ResourceResearchController.BlindSearchTooDangerous(destination, deaths))
                        await radars.EnsureAsync(catalog, controller, token);
                    else await radars.ChartAsync([resource], charted, charting, catalog, controller, token);
                }
                catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
                {
                    await journal.AppendAsync("radar-failed", new { resource, purpose = "resource-discovery", error = error.GetType().Name, error.Message }, token);
                }
                map = await CaptureAsync();
                deaths = await DeathsAsync(map);
                if (Find(map, deaths) is { } radarObserved) return await CompleteAsync(radarObserved, map, steps);
                destination = await DestinationAsync(map, deaths);
            }
            if (ResourceResearchController.BlindSearchTooDangerous(destination, deaths))
                throw new ExplorationTooDangerousException($"No safe remembered or charted {resource} destination; {deaths.Count} recent own deaths refuse a blind exploration step.");
            await new SurvivalKitController(game, journal).BeforeTripAsync("resource-discovery", token);
            var waypoint = await controller.FindExplorationWaypointAsync(exploration, catalog, "", destination?.Position, token,
                avoidDestinationDeathZones: true);
            await journal.AppendAsync("resource-discovery-search", new { resource, steps, map.Scope, map.CollectedTick, destination, waypoint }, token);
            await controller.NavigateAsync(waypoint.Position, cancellationToken: token);
        }
        var progress = new ResourceSearchProgressResult(resource, startTick, lastMap!.CollectedTick, MaximumSearchSteps,
            truncatedHistory ? 0 : observedCells.Except(previousCells!).Count(), truncatedHistory);
        await journal.AppendAsync("resource-discovery-incomplete", new { progress, lastMap.Scope, lastMap.SurfaceIndex,
            evidence = "native-local-coverage", interpretation = "No deposit discovered. Only complete local actor observations count; global absence and reachability remain unproven." }, token);
        return new(Progress: progress);

        async Task<SpatialSnapshot> CaptureAsync()
        {
            var map = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            if (map.Scope != catalog.Scope || surface is { } expected && map.SurfaceIndex != expected)
                throw new InvalidDataException("Actor or surface changed during resource discovery; reconcile partial travel.");
            if (map.CollectedTick < (lastMap?.CollectedTick ?? startTick))
                throw new InvalidDataException("Resource discovery observation predates its catalog or earlier local view.");
            if (!map.Coverage.Atomic || !map.Coverage.Complete || map.Coverage.Visibility != "current-character-local-area")
                throw new InvalidDataException("Resource discovery requires a complete normal local observation.");
            surface ??= map.SurfaceIndex;
            if (previousCells is null)
            {
                var history = game is IResourceMemoryReader reader ? await reader.ReadResourceMemoryAsync(map, token)
                    : ResourceMemorySnapshot.Empty(map);
                history.ValidateFor(map);
                previousCells = history.SurveyedCells.ToHashSet();
                // The starting photograph establishes the baseline even when an older client has no history writer.
                previousCells.UnionWith(ResourceMemorySnapshot.LocalCells(map));
                truncatedHistory = history.Truncated;
            }
            observedCells.UnionWith(ResourceMemorySnapshot.LocalCells(map));
            if (observedCells.Union(previousCells).Take(100001).Count() > 100000) truncatedHistory = true;
            lastMap = map;
            return map;
        }

        async Task<IReadOnlyList<NativeDeathTransition>> DeathsAsync(SpatialSnapshot map) => game is IDangerZoneReader danger
            ? (await danger.ReadActiveDeathsAsync(map.Scope, map.SurfaceIndex, map.CollectedTick, token))
                .Where(d => d.SurfaceIndex == map.SurfaceIndex && DangerZones.ActiveAt(d, map.CollectedTick)).ToArray() : [];

        SpatialEntity? Find(SpatialSnapshot map, IReadOnlyList<NativeDeathTransition> deaths) => map.Entities
            .Where(e => e.Name == resource && e.Amount is > 0 && double.IsFinite(e.Amount.Value)
                && map.Prototypes.TryGetValue(e.Name, out var geometry) && geometry.Type == "resource"
                && !deaths.Any(d => DangerZones.Covers(d, e.Position)))
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();

        async Task<ResourceSighting?> DestinationAsync(SpatialSnapshot map, IReadOnlyList<NativeDeathTransition> deaths)
        {
            if (game is not IResourceMemoryReader reader) return null;
            var memory = await reader.ReadResourceMemoryAsync(map, token);
            memory.ValidateFor(map);
            return memory.NearestOf(resource, map.Actor.Position, r => !map.Bounds.Contains(r.Position)
                && !deaths.Any(d => DangerZones.Covers(d, r.Position)));
        }

        async Task<ResourceDiscoveryAttempt> CompleteAsync(SpatialEntity deposit, SpatialSnapshot map, int steps)
        {
            var result = new ResourceDiscoveryResult(resource, deposit.Id, deposit.Position, deposit.Amount!.Value,
                startTick, map.CollectedTick, steps);
            await journal.AppendAsync("resource-discovery-result", new { result, map.Scope, map.SurfaceIndex,
                evidence = "current-native-local-resource", interpretation = "Observed deposit; extraction site, route, power and output are not proven." }, token);
            return new(Discovery: result);
        }
    }
}
