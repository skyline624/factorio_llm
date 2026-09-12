using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionGoalExecutorTests
{
    [Fact]
    public async Task AlreadySatisfiedStockDoesNotStartAnotherProductionBatch()
    {
        var game = new SelectionGame(stock: 20, installed: true);
        var result = await new ProductionGoalExecutor(game, new Journal()).RunAsync("plate", 20);
        Assert.Equal("already-satisfied", result.Method);
        Assert.Equal(20, result.FinalStock);
        Assert.Equal(new[] { "observe" }, game.Calls);
    }

    [Theory]
    [InlineData(true, "automated-smelting")]
    [InlineData(false, "actor-production")]
    public async Task SelectsFromNativeCapabilityAndPropagatesExecutionFailureWithoutFallback(bool installed, string method)
    {
        var game = new SelectionGame(stock: 0, installed);
        var journal = new Journal();
        await Assert.ThrowsAsync<IOException>(() => new ProductionGoalExecutor(game, journal).RunAsync("plate", 20));
        var selection = Assert.Single(journal.Selections);
        Assert.Equal(method, selection.GetProperty("method").GetString());
        // Second observation is the selected executor recollecting state, then the transport fails.
        // Any fallback/retry would make another observation and mask an unresolved state.
        Assert.Equal(new[] { "observe", "production_catalog", "spatial", "observe" }, game.Calls);
    }

    [Fact]
    public async Task InvalidAssessmentDoesNotBecomeManualFallback()
    {
        var game = new SelectionGame(stock: 0, installed: true) { MismatchedScope = true };
        var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProductionGoalExecutor(game, journal).RunAsync("plate", 20));
        Assert.Empty(journal.Selections);
        Assert.Equal(new[] { "observe", "production_catalog" }, game.Calls);
    }

    private sealed class Journal : IControllerJournal
    {
        public List<JsonElement> Selections { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "production-method") Selections.Add(Protocol.ToElement(data));
            return Task.CompletedTask;
        }
    }

    private sealed class SelectionGame(long stock, bool installed) : IGameClient
    {
        public bool MismatchedScope { get; init; }
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            var (map, catalog) = SmeltingPlannerTests.Setup(installed);
            if (request.Action == "observe" && Calls.Count > 1) throw new IOException("Native execution observation unavailable.");
            object data = request.Action switch
            {
                "observe" => new
                {
                    scope = map.Scope,
                    collectedTick = 1,
                    coverage = new { knownInventoriesComplete = true },
                    agent = new { alive = true, controlMode = "ai", inventory = new Dictionary<string, long> { ["plate"] = stock } },
                    entities = map.Entities.Where(e => e.Force == "agent").Select(e => new
                    { e.Id, e.Name, e.Position, recipe = (string?)null, inventories = new { } }).ToArray()
                },
                "production_catalog" => MismatchedScope ? catalog with { Scope = catalog.Scope with { Generation = 2 } } : catalog,
                "spatial" => map,
                _ => throw new InvalidOperationException($"Unexpected mutation or query: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 1, Protocol.ToElement(data)));
        }
    }
}
