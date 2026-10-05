using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CraftInventoryTests
{
    private static readonly NativeRecipe Recipe = new("underground-belt", true, "crafting", 1,
        [new("iron-plate", "item", 10), new("transport-belt", "item", 5)], [new("underground-belt", "item", 2)], false);

    [Fact]
    public void TheEntireCraftRequestAndTheOtherConstructionPartsAreRetained()
    {
        var retained = CraftInventoryController.Retained(Recipe, 6,
            new Dictionary<string, int> { ["iron-plate"] = 100, ["small-electric-pole"] = 9 });
        Assert.Equal(100, retained["iron-plate"]);
        Assert.Equal(30, retained["transport-belt"]);
        Assert.Equal(9, retained["small-electric-pole"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void AnUnboundedCraftCannotDriveAnInventoryMutation(int batches) =>
        Assert.Throws<InvalidDataException>(() => CraftInventoryController.Retained(Recipe, batches, new Dictionary<string, int>()));

    [Fact]
    public void NonSolidOrRandomCraftProductsAreRejectedBeforeStoragePlanning() =>
        Assert.Throws<InvalidDataException>(() => CraftInventoryController.Retained(Recipe with
        { Products = [new("underground-belt", "item", 2, Probability: .5)] }, 1, new Dictionary<string, int>()));

    [Fact]
    public async Task NestedProductionInheritsTheLatestFactoryAndRestoresItAfterwards()
    {
        var outer = new FactoryState(1, "world", [], []);
        var inner = outer with { WorldId = "other" };
        Assert.Null(ProductionReservations.Factory);
        using (ProductionReservations.EnterFactory(outer))
        {
            using (ProductionReservations.Enter(new HashSet<string> { "machine" }))
            {
                await Task.Yield();
                Assert.Same(outer, ProductionReservations.Factory);
            }
            using (ProductionReservations.EnterFactory(inner)) Assert.Same(inner, ProductionReservations.Factory);
            Assert.Same(outer, ProductionReservations.Factory);
        }
        Assert.Null(ProductionReservations.Factory);
    }

    [Fact]
    public async Task ExistingOutputRoomRequiresOneCompleteCapacityReadAndNoActorWork()
    {
        var game = new CapacityGame(room: true);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        Assert.False(await new CraftInventoryController(game, journal).PrepareAsync(Recipe, 20, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
        Assert.Empty(journal.Types);
    }

    [Fact]
    public async Task FullInventoryWithoutAnObservedStorageHomeIsDeferredBeforeAnyCraft()
    {
        var game = new CapacityGame(room: false);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CraftInventoryController(game, journal)
            .PrepareAsync(Recipe, 1, game.Catalog, controller, default));
        Assert.All(game.Actions, action => Assert.Equal("factory_snapshot", action));
        Assert.Equal(["craft-inventory-room-unavailable"], journal.Types);
    }

    [Fact]
    public async Task ExistingRoomForCollectedStockDoesNotMoveOrDeposit()
    {
        var game = new CapacityGame(room: true);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        Assert.False(await new CraftInventoryController(game, journal)
            .PrepareCollectionAsync("underground-belt", 2, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
        Assert.Empty(journal.Types);
    }

    [Fact]
    public async Task AFullBagCannotTakeExistingStockWithoutAnObservedStorageHome()
    {
        // Normal seed20261072: a full bag after recovery refused200cables needed by the first coal drill.
        var game = new CapacityGame(room: false);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CraftInventoryController(game, journal)
            .PrepareCollectionAsync("underground-belt", 2, game.Catalog, controller, default));
        Assert.All(game.Actions, action => Assert.Equal("factory_snapshot", action));
        Assert.Equal(["stock-collection-inventory-room-unavailable"], journal.Types);
    }

    [Fact]
    public async Task ARespawnedActorsCapacityCannotAuthorizeACollection()
    {
        var game = new CapacityGame(room: true, changedScope: true);
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => new CraftInventoryController(game, new Journal())
            .PrepareCollectionAsync("underground-belt", 2, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
    }

    [Fact]
    public async Task CapacityFromAnotherIncarnationCannotAuthorizeStorageOrCrafting()
    {
        var game = new CapacityGame(room: true, changedScope: true);
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => new CraftInventoryController(game, new Journal())
            .PrepareAsync(Recipe, 1, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
    }

    [Fact]
    public async Task AMiningBootstrapWithNativeRoomDoesNotVisitStorage()
    {
        var game = new CapacityGame(room: true, probeItem: "wood", probeCapacity: 100);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        Assert.False(await new CraftInventoryController(game, journal).PrepareMiningAsync("wood", 1, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
        Assert.Empty(journal.Types);
    }

    [Fact]
    public async Task MiningOneRequestedItemStillNeedsRoomForANativeTreeYield()
    {
        var game = new CapacityGame(room: true, probeItem: "wood", probeCapacity: 1);
        var journal = new Journal();
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CraftInventoryController(game, journal)
            .PrepareMiningAsync("wood", 1, game.Catalog, controller, default));
        Assert.All(game.Actions, action => Assert.Equal("factory_snapshot", action));
        Assert.Equal(["mining-inventory-room-unavailable"], journal.Types);
    }

    [Fact]
    public async Task AChangedActorCannotAuthorizeMiningCapacity()
    {
        var game = new CapacityGame(room: true, changedScope: true, probeItem: "wood", probeCapacity: 100);
        using var factory = ProductionReservations.EnterFactory(new(1, game.Catalog.Scope.WorldId, [], []));
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => new CraftInventoryController(game, new Journal())
            .PrepareMiningAsync("wood", 1, game.Catalog, controller, default));
        Assert.Equal(["factory_snapshot"], game.Actions);
    }

    [Fact]
    public async Task StandaloneMiningKeepsItsNativeCapacityChecks()
    {
        var game = new CapacityGame(room: true, probeItem: "wood", probeCapacity: 100);
        await using var controller = new SpatialController(game, new Journal());
        Assert.False(await new CraftInventoryController(game, new Journal()).PrepareMiningAsync("wood", 1, game.Catalog, controller, default));
        Assert.Empty(game.Actions);
    }

    private sealed class CapacityGame(bool room, bool changedScope = false, string probeItem = "underground-belt", int probeCapacity = 50) : IGameClient
    {
        public ProductionCatalog Catalog { get; } = Catalogs.Raw() with
        { Items = new Dictionary<string, NativeItem>(Catalogs.Raw().Items) { ["underground-belt"] = new(0, 50), ["wood"] = new(0, 100) } };
        public List<string> Actions { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            Assert.Equal("factory_snapshot", request.Action);
            var scope = changedScope ? Catalog.Scope with { Incarnation = Catalog.Scope.Incarnation + 1 } : Catalog.Scope;
            var records = new FactoryRecord[]
            {
                new("actor", "entity", "actor", "character", Protocol.ToElement(new { role = "actor", mainInventoryId = "main", position = new MapPosition(0, 0) })),
                new("main", "inventory", "actor", "main", Protocol.ToElement(new
                {
                    items = room ? new Dictionary<string, long>() : new Dictionary<string, long> { ["iron-plate"] = 100 },
                    slots = 1, usableSlots = 1, filters = new { },
                    stacks = room ? [] : new[] { new { slot = 1, name = "iron-plate", quality = "normal", count = 100 } },
                    capacityHints = new Dictionary<string, object> { [probeItem] = new
                    { insertable = room ? probeCapacity : 0, canInsertOne = room, certainty = "native-estimate" } }
                }))
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(new
            {
                snapshotId = "capacity", scope, snapshotScope = scope, collectedTick = 100, expiresTick = 3700,
                totalRecords = records.Length, offset = 0, nextOffset = records.Length, complete = true,
                coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                records
            })));
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token) { Types.Add(type); return Task.CompletedTask; }
    }
}
