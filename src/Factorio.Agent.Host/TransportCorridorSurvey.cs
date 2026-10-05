using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Walks and observes a bounded corridor with the controlled actor; it never requests wider native visibility.</summary>
internal sealed class TransportCorridorSurvey(IGameClient game, IControllerJournal journal)
{
    internal const int MaximumSpan = 1024;
    internal const int Step = 48;
    internal const int MaximumSamples = 32;
    internal const int MaximumBelts = 1000;

    internal static bool CanSurvey(FactorySnapshot snapshot, IReadOnlyList<string> required)
    {
        if (required.Count < 2) return false;
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
        await ReadAsync();
        int segments = Math.Max(1, (int)Math.Ceiling(source.DistanceTo(target) / Step));
        for (int i = 1; i < segments; i++)
        {
            var waypoint = new MapPosition(source.X + (target.X - source.X) * i / segments, source.Y + (target.Y - source.Y) * i / segments);
            await controller.TravelAsync(waypoint, 8, catalog, token);
            await ReadAsync();
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

        async Task ReadAsync()
        {
            var observed = await spatial.CaptureAsync(items, 48, token);
            if (observed.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during transport survey.");
            atlas.Add(observed);
        }
    }
}
