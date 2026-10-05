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

    [Fact]
    public async Task EmptyDirectOutputStorageOwnsTheMachineAndItsInserterHand()
    {
        var map = DirectMap();
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0);
        stock = stock with { Records = stock.Records.Select(r => r.Id == "extract:hand"
            ? r with { Data = Protocol.ToElement(new { items = new { gear = 3 } }) } : r).ToArray() };
        var game = new MapGame(map);
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        var result = await controller.TryCollectOutputAsync("gear", 5, stock, CancellationToken.None);
        Assert.True(result.Connected);
        Assert.False(result.Collected);
        Assert.Equal(5, result.Pending);
        Assert.Equal(3, result.InTransit);
        Assert.Equal(["spatial"], game.Calls);
    }

    [Theory]
    [InlineData("second-extractor")]
    [InlineData("second-direct-extractor")]
    [InlineData("foreign-inserter")]
    [InlineData("unpowered-inserter")]
    [InlineData("missing-hand")]
    [InlineData("different-hand-item")]
    public async Task AnUnprovenDirectOutputCannotAuthorizeCollection(string mutation)
    {
        var map = DirectMap();
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0);
        if (mutation == "second-extractor")
            map = map with { Entities = [.. map.Entities, map.Entities.Single(e => e.Id == "extract") with { Id = "extra", DropTargetId = "unknown" }] };
        if (mutation == "second-direct-extractor")
            map = map with { Entities = [.. map.Entities, map.Entities.Single(e => e.Id == "extract") with { Id = "extra" }] };
        if (mutation == "foreign-inserter")
            map = map with { Entities = map.Entities.Select(e => e.Id == "extract" ? e with { Force = "foreign" } : e).ToArray() };
        if (mutation == "unpowered-inserter")
            map = map with { Entities = map.Entities.Select(e => e.Id == "extract" ? e with { Power = null } : e).ToArray() };
        if (mutation == "missing-hand") stock = stock with { Records = stock.Records.Where(r => r.Id != "extract:hand").ToArray() };
        if (mutation == "different-hand-item") stock = stock with { Records = stock.Records.Select(r => r.Id == "extract:hand"
            ? r with { Data = Protocol.ToElement(new { items = new { other = 1 } }) } : r).ToArray() };
        var game = new MapGame(map);
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.TryCollectOutputAsync("gear", 5, stock, CancellationToken.None));
        Assert.Equal(["spatial"], game.Calls);
    }

    [Fact]
    public async Task AnEmptyDirectOutputStillCannotFeedReservedNestedStock()
    {
        var map = DirectMap();
        var game = new MapGame(map);
        using var reservation = ProductionReservations.Enter(new HashSet<string> { "target" });
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TryCollectOutputAsync("gear", 5,
            BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0), CancellationToken.None));
        Assert.Equal(["spatial"], game.Calls);
    }

    [Fact]
    public async Task DirectOutputStockRequiresTheCurrentActorScope()
    {
        var map = DirectMap();
        var game = new MapGame(map);
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0) with { Scope = map.Scope with { Incarnation = map.Scope.Incarnation + 1 } };
        await using var spatial = new SpatialController(game, new Journal());
        var controller = new AssemblyTransportController(game, new Journal(), BeltTransportBoundaryTests.Catalog(map), "root", spatial);
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.TryCollectOutputAsync("gear", 5, stock, CancellationToken.None));
    }

    private static SpatialSnapshot DirectMap()
    {
        var map = BeltTransportBoundaryTests.Map();
        var arm = map.Entities.Single(e => e.Id == "extract") with { PickupTargetId = "root", DropTargetId = "target" };
        return map with { Entities = [.. map.Entities.Where(e => e.Id == "root" || e.Id == "target" || e.Id == map.Actor.Id), arm] };
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
