using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportDemandTests
{
    [Theory]
    [InlineData(100, 0, 100, 25)]
    [InlineData(10, 4, 100, 6)]
    [InlineData(10, 10, 100, 0)]
    [InlineData(0, 0, 100, 0)]
    [InlineData(100, 0, 1, 1)]
    public void SourceReserveLeavesABoundedLotForConsumersStillServedByTheActor(long needed, long carried, int stackSize, int expected) =>
        Assert.Equal(expected, FactoryTransportBuilder.ActorReserve(needed, carried, stackSize));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStockedConsumerDoesNotStartTransportProcurementOrMovement(bool changedActor)
    {
        string directory = Directory.CreateTempSubdirectory("transport-demand-").FullName;
        try
        {
            var catalog = Catalogs.Early();
            catalog = catalog with { Recipes = [.. catalog.Recipes,
                new("transport-belt", true, "crafting", 1, [new("iron-plate", "item", 1)], [new("transport-belt", "item", 1)], false),
                new("small-electric-pole", true, "crafting", 1, [new("copper-cable", "item", 1)], [new("small-electric-pole", "item", 1)], false)] };
            var consumer = new FactoryCell("red", 1, new(0, 0, true), "assembler", "assembling-machine-1", "automation-science-pack",
                new Dictionary<string, string> { ["input-chest"] = "red-in", ["output-chest"] = "red-out" }, "ready", 1);
            var producer = consumer with { Id = "gears", Recipe = "iron-gear-wheel", Entities = new Dictionary<string, string> { ["output-chest"] = "gears-out" } };
            await new FactoryRegistry(directory).SaveAsync(new FactoryState(1, catalog.Scope.WorldId, [], [consumer, producer])
                .WithTarget("automation-science-pack", 8), default);
            var game = new StockedGame(changedActor ? catalog.Scope with { Generation = catalog.Scope.Generation + 1 } : catalog.Scope);
            var builder = new FactoryTransportBuilder(game, new ControllerJournal(Path.Combine(directory, "journal.jsonl")), directory);
            if (changedActor) await Assert.ThrowsAsync<InvalidDataException>(() => builder.ConnectAsync(catalog));
            else Assert.Equal(0, await builder.ConnectAsync(catalog));
            Assert.Equal(1, game.SnapshotCalls);
            Assert.Null((await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, default)).Transports);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class StockedGame(ActorScope scope) : IGameClient
    {
        public int SnapshotCalls { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("factory_snapshot", request.Action); // Procurement, spatial observation and mutations are forbidden in this scenario.
            SnapshotCalls++;
            FactoryRecord[] records =
            [new("red-in", "entity", "red-in", "iron-chest", Protocol.ToElement(new { position = new MapPosition(8, 0) })),
             new("gears-out", "entity", "gears-out", "iron-chest", Protocol.ToElement(new { position = new MapPosition(0, 0) })),
             new("red-stock", "inventory", "red-out", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { ["automation-science-pack"] = 200 } }))];
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(new
            {
                snapshotId = "native-stock", scope, snapshotScope = scope, collectedTick = 100, expiresTick = 3700,
                totalRecords = records.Length, offset = 0, nextOffset = records.Length, complete = true,
                coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }, records
            })));
        }
    }
}
