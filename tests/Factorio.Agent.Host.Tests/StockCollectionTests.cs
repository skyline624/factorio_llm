using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StockCollectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExhaustedOutputsReturnTheActualStockWithoutMiningOrManufacturing(long carried)
    {
        var game = new EmptyStoreGame(carried);
        var journal = new Journal();
        var result = await new ProductionController(game, journal).CollectAvailableAsync("wood", 15);
        Assert.Equal(carried, result.FinalStock);
        Assert.Equal(15, result.TargetStock);
        Assert.Empty(result.Receipts);
        Assert.All(game.Calls, action => Assert.Equal("observe", action));
        Assert.Equal(new[] { "stock-collection-result" }, journal.Types);
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyStoreGame(long carried) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            if (request.Action != "observe") throw new InvalidOperationException("Stock collection attempted production or another unexpected operation.");
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(new
            {
                scope = new ActorScope("world", "session", "actor", 1, 1),
                collectedTick = 100,
                coverage = new { knownInventoriesComplete = true },
                agent = new { alive = true, controlMode = "ai", inventory = new Dictionary<string, long> { ["wood"] = carried } },
                entities = Array.Empty<object>()
            })));
        }
    }
}
