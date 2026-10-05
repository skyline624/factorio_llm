using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Walks and observes a bounded corridor with the controlled actor; it never requests wider native visibility.</summary>
internal sealed class TransportCorridorSurvey(IGameClient game, IControllerJournal journal)
{
    internal const int MaximumSpan = 1024;
    internal const int Step = 28;
    internal const int MaximumSamples = 32;
    internal const int MaximumBelts = 1000;

    internal static bool CanSurvey(FactorySnapshot snapshot, IReadOnlyList<string> required)
    {
        if (required.Distinct(StringComparer.Ordinal).Count() < 2) return false;
        var positions = required.Distinct(StringComparer.Ordinal).Select(id => FactoryTransportBuilder.Position(snapshot, id)).ToArray();
        if (positions.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return false;
        double width = positions.Max(p => p!.X) - positions.Min(p => p!.X);
        double height = positions.Max(p => p!.Y) - positions.Min(p => p!.Y);
        return width <= MaximumSpan && height <= MaximumSpan && (width + 113) * (height + 113) <= SurveyedTransportFrame.MaximumTiles
            && Math.Ceiling(positions[0]!.DistanceTo(positions[1]!) / Step) + 2 <= MaximumSamples;
    }

    public async Task<SpatialSnapshot?> CaptureAsync(FactorySnapshot snapshot, IReadOnlyList<string> required, string[] items,
        ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (!CanSurvey(snapshot, required)) return null;
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Transport survey input belongs to another actor.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12)); token = deadline.Token;
        var source = FactoryTransportBuilder.Position(snapshot, required[0])!;
        var target = FactoryTransportBuilder.Position(snapshot, required[1])!;
        var atlas = new SurveyedTransportFrame();
        var spatial = new SpatialClient(game);
        await controller.ApproachEntityAsync(required[0], source, catalog, token);
        var observed = await ReadAsync();
        var exploration = new ExplorationPlanner();
        if (observed.Actor.Position.DistanceTo(target) > 24)
            await new SurvivalKitController(game, journal).BeforeTripAsync("transport-survey", token);
        while (observed.Actor.Position.DistanceTo(target) > 24)
        {
            if (atlas.Samples >= MaximumSamples - 1)
            {
                await journal.AppendAsync("factory-transport-corridor-limited", new { observed.Scope, observed.CollectedTick,
                    samples = atlas.Samples, maximumSamples = MaximumSamples, observed.Actor.Position, target,
                    reason = "sample-budget", completeEndpoints = false }, token);
                return null;
            }
            // A straight interpolated point can lie in a lake. Use the same observed, reachable local
            // steps as ordinary travel, and photograph each completed step rather than its intended line.
            var next = await controller.FindExplorationWaypointAsync(exploration, catalog, "", target, token);
            await controller.NavigateAsync(next.Position, cancellationToken: token);
            observed = await ReadAsync();
        }
        await controller.ApproachEntityAsync(required[1], target, catalog, token);
        await ReadAsync();
        var map = atlas.Build();
        bool completeEndpoints = required.All(id => map.Entities.Any(e => e.Id == id));
        await journal.AppendAsync("factory-transport-corridor", new
        {
            map.Scope, firstTick = atlas.FirstTick, map.CollectedTick, samples = atlas.Samples,
            map.Bounds, map.Coverage, requiredEntities = required.Count, completeEndpoints,
            nativeRadius = 48, unknownTilesBlocked = true, maximumBelts = MaximumBelts
        }, token);
        return completeEndpoints ? map : null;

        async Task<SpatialSnapshot> ReadAsync()
        {
            var observed = await spatial.CaptureAsync(items, 48, token);
            if (observed.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during transport survey.");
            atlas.Add(observed);
            return observed;
        }
    }
}
