using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class VanishedTargetLogisticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"vanished-target-{Guid.NewGuid():N}");

    [Fact]
    public async Task ATransferTargetGoneFromItsKnownPositionIsSkippedInsteadOfFailingTheRound()
    {
        // Campaign 2026-10-01 (seed 20261002): a furnace seen in the factory photograph was gone (biters) when the actor
        // arrived to fuel it; "Sequence contains no matching element" failed the whole research goal.
        Directory.CreateDirectory(directory);
        var catalog = Catalogs.Raw();
        var cell = new FactoryCell("iron", 0, new(1, 0, true), "smelter", "burner-mining-drill", "iron-plate",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["furnace"] = "f1", ["output-chest"] = "o1" }, "ready", 1);
        await new FactoryRegistry(directory).SaveAsync(new(1, catalog.Scope.WorldId, [], [cell]), default);
        FactoryRecord[] records =
        [
            Record("actor", "entity", "actor", new { role = "actor", type = "character", position = new MapPosition(0, 0), mainInventoryId = "actor-main" }),
            Record("actor-main", "inventory", "actor", new { items = new Dictionary<string, long> { ["coal"] = 30 } }),
            Record("f1", "entity", "f1", new { role = "factory", type = "furnace", position = new MapPosition(1, 1), fuelInventoryId = "f1-fuel" }),
            Record("f1-fuel", "inventory", "f1", new { items = new Dictionary<string, long>() }),
            Record("o1", "entity", "o1", new { role = "factory", type = "container", position = new MapPosition(1, 3.5) })
        ];
        var game = new Game(catalog, FactoryMaps.Grass(4), records);
        var journal = new Journal();
        var result = await new FactoryLogistics(game, journal, directory).ServiceAsync();
        Assert.Contains("factory-transfer-target-missing", journal.Types);
        Assert.DoesNotContain("submit", game.Calls);
        Assert.Equal(0, result.Supplied.GetValueOrDefault("coal"));
    }

    public void Dispose() => Directory.Delete(directory, true);

    private static FactoryRecord Record(string id, string kind, string entityId, object data) => new(id, kind, entityId, id, Protocol.ToElement(data));

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    /// <summary>Serves the catalog, an empty local map (the furnace is gone) and the older factory photograph; mutations fail.</summary>
    private sealed class Game(ProductionCatalog catalog, SpatialSnapshot map, IReadOnlyList<FactoryRecord> records) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            (object data, long tick) = request.Action switch
            {
                "production_catalog" => ((object)catalog, catalog.CollectedTick),
                "spatial" => (map, map.CollectedTick),
                "factory_snapshot" => (new
                {
                    snapshotId = $"s{Calls.Count}", scope = catalog.Scope, snapshotScope = catalog.Scope, collectedTick = 100L, expiresTick = 1000L,
                    totalRecords = records.Count, offset = 0, nextOffset = records.Count, complete = true,
                    coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                    records
                }, 100L),
                _ => throw new InvalidOperationException($"Unexpected request: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
