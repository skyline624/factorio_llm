using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AssemblyOutputRoutingTests
{
    [Fact]
    public async Task EmptyConnectedStorageStillOwnsTheMachinesOutput()
    {
        var map = BeltTransportBoundaryTests.Map();
        var game = new MapGame(map);
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        var result = await controller.TryCollectOutputAsync("gear", 5, BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0), CancellationToken.None);
        Assert.True(result.Connected);
        Assert.False(result.Collected);
        Assert.Equal(2, result.Pending);
        Assert.Equal(new[] { "spatial" }, game.Calls);
    }

    [Fact]
    public async Task AnUnreconciledOutputExtractorDoesNotPermitDirectCollection()
    {
        var map = BeltTransportBoundaryTests.Map();
        map = map with { Entities = map.Entities.Select(e => e.Id == "up" ? e with { BeltConnections = new([], ["unknown"]) } : e).ToArray() };
        var game = new MapGame(map);
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TryCollectOutputAsync("gear", 5,
            BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0), CancellationToken.None));
    }

    private sealed class MapGame(SpatialSnapshot map) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            if (request.Action != "spatial") throw new InvalidOperationException("No actor transfer is allowed while connected storage is empty.");
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(map)));
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
}
