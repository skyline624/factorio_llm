using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StockCollectionWindowTests
{
    [Fact]
    public async Task ARefillingProducerReturnsTheInitiallyObservedStockBeforeProductionAssessment()
    {
        var game = new RefillingStore();
        var result = await new ProductionController(game, new Journal()).CollectAvailableAsync("iron-plate", 40);
        Assert.Equal(5, result.FinalStock);
        Assert.Equal(40, result.TargetStock);
        Assert.Equal([5L], game.Requested);
    }

    [Fact]
    public async Task APartialTransferUsesActualMovedStockToFinishTheObservedBatch()
    {
        var game = new RefillingStore { PartialFirstTake = true };
        var result = await new ProductionController(game, new Journal()).CollectAvailableAsync("iron-plate", 40);
        Assert.Equal(5, result.FinalStock);
        Assert.Equal([5L, 3L], game.Requested);
    }

    [Fact]
    public async Task ReservedOutputsCannotExtendTheFiniteCollection()
    {
        var game = new RefillingStore { ReservedSource = true };
        var result = await new ProductionController(game, new Journal()).CollectAvailableAsync("iron-plate", 40,
            reservedEntityIds: new HashSet<string> { "reserved" });
        Assert.Equal(5, result.FinalStock);
        Assert.Equal([5L], game.Requested);
    }

    [Fact]
    public async Task ObservedStockCanMoveToAnotherChestWithoutCreatingAnotherBatch()
    {
        var game = new RefillingStore { ShiftSource = true };
        var result = await new ProductionController(game, new Journal()).CollectAvailableAsync("iron-plate", 40);
        Assert.Equal(5, result.FinalStock);
        Assert.Equal(["receiver"], game.TakenSources);
    }

    [Fact]
    public async Task MismatchedTransferEffectsCannotAuthorizeAnotherTake()
    {
        var game = new RefillingStore { WrongReceiptItem = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProductionController(game, new Journal())
            .CollectAvailableAsync("iron-plate", 40));
        Assert.Single(game.Requested);
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    // Synthetic native protocol fixture: each take is followed by five newly produced items.
    private sealed class RefillingStore : IGameClient
    {
        private readonly SpatialSnapshot map = FactoryMaps.Grass(8);
        private long carried;
        private int observations;
        public bool PartialFirstTake { get; init; }
        public bool ReservedSource { get; init; }
        public bool ShiftSource { get; init; }
        public bool WrongReceiptItem { get; init; }
        public List<long> Requested { get; } = [];
        public List<string> TakenSources { get; } = [];

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            object data;
            if (request.Action == "production_catalog")
                data = Catalogs.Raw() with { Scope = map.Scope, CollectedTick = map.CollectedTick };
            else if (request.Action == "spatial") data = map;
            else if (request.Action == "observe")
            {
                observations++;
                bool shifted = ShiftSource && observations > 1;
                var stores = new List<object> { Store("source", shifted ? 0 : 5) };
                if (ShiftSource) stores.Add(Store("receiver", shifted ? 5 : 0));
                if (ReservedSource) stores.Add(Store("reserved", 100));
                data = new
                {
                    scope = map.Scope, collectedTick = map.CollectedTick,
                    coverage = new { knownInventoriesComplete = true, atomic = true, collectionStartTick = map.CollectedTick,
                        collectionEndTick = map.CollectedTick, enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", position = map.Actor.Position, health = 250, maxHealth = 250,
                        stopUnconfirmed = false, inventory = new Dictionary<string, long> { ["iron-plate"] = carried },
                        weapon = new { ready = false, rounds = 0 } },
                    enemies = Array.Empty<object>(), entities = stores
                };
            }
            else if (request.Action == "submit")
            {
                var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                Assert.Equal("take", submission.Kind);
                Assert.Equal("iron-plate", submission.Args.GetProperty("item").GetString());
                long count = submission.Args.GetProperty("count").GetInt64();
                string source = submission.Args.GetProperty("entityId").GetString()!;
                Requested.Add(count); TakenSources.Add(source);
                long moved = PartialFirstTake && Requested.Count == 1 ? 2 : count;
                carried += moved;
                data = new OperationReceipt(submission.OperationId, "take", moved == count ? "completed" : "partial",
                    map.CollectedTick, map.CollectedTick, Protocol.ToElement(new { targetId = source,
                        item = WrongReceiptItem ? "coal" : "iron-plate", requested = count, transferred = moved, direction = "to_actor" }),
                    null, Protocol.ToElement(new { }));
            }
            else throw new InvalidOperationException("Unexpected operation: " + request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(data)));
        }

        private object Store(string id, long stock) => new { id, name = "iron-chest", position = map.Actor.Position,
            recipe = (string?)null, inventories = new { output = new { items = new Dictionary<string, long> { ["iron-plate"] = stock } } } };
    }
}
