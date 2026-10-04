using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceBandLogisticsTests
{
    // 40 steel crafts keep a 15-coal reserve in the chest; a quarter of a 50-coal stack is 12.
    [Theory]
    [InlineData(14, 0, 0)]
    [InlineData(12, 0, 0)]
    [InlineData(8, 5, 0)]
    [InlineData(3, 0, 9)]
    [InlineData(0, 0, 12)]
    public void LowBandFurnaceSupplyProcuresOnlyItsQuarterStackGap(long chest, long furnace, long shortfall)
    {
        using var world = new World(chest, furnace);
        var result = world.Service();
        Assert.Equal(shortfall, result.Shortfall.GetValueOrDefault("coal"));
        Assert.Equal(shortfall, result.FuelShortfall);
        Assert.Empty(result.Supplied);
    }

    /// <summary>One registered steel band cell with a full plate buffer, the coal its chest and furnace hold, and none carried.</summary>
    private sealed class World : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"furnace-band-logistics-{Guid.NewGuid():N}");
        public LogisticsGame Game { get; }

        public World(long chest, long furnace)
        {
            Directory.CreateDirectory(directory);
            var catalog = Catalogs.Raw();
            var cell = new FactoryCell("steel", 1, new(0, 0, true), FurnaceCellPlanner.Kind, "stone-furnace", "steel-plate",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["machine"] = "f1", ["input-chest"] = "c1", ["output-chest"] = "o1" },
                "ready", 1);
            new FactoryRegistry(directory).SaveAsync(new(1, catalog.Scope.WorldId, [], [cell]), CancellationToken.None).GetAwaiter().GetResult();
            FactoryRecord[] records =
            [
                Record("actor", "entity", "actor", new { role = "actor", type = "character", position = new MapPosition(0, 0), mainInventoryId = "actor-main" }),
                Record("actor-main", "inventory", "actor", new { items = new Dictionary<string, long>() }),
                Record("f1", "entity", "f1", new { role = "factory", type = "furnace", position = new MapPosition(1, 1), fuelInventoryId = "f1-fuel" }),
                Record("f1-fuel", "inventory", "f1", new { items = new Dictionary<string, long> { ["coal"] = furnace } }),
                Record("c1", "entity", "c1", new { role = "factory", type = "container", position = new MapPosition(1, -1.5) }),
                Record("c1-chest", "inventory", "c1", new { items = new Dictionary<string, long> { ["iron-plate"] = 200, ["coal"] = chest } }),
                Record("o1", "entity", "o1", new { role = "factory", type = "container", position = new MapPosition(1, 3.5) })
            ];
            var map = FactoryMaps.Grass(4);
            map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["stone-furnace"] = FurnaceBandTests.StoneFurnace() } };
            Game = new(catalog, map, records);
        }

        public LogisticsResult Service() => new FactoryLogistics(Game, new Journal(), directory).ServiceAsync().GetAwaiter().GetResult();

        public void Dispose() => Directory.Delete(directory, true);

        private static FactoryRecord Record(string id, string kind, string entityId, object data) => new(id, kind, entityId, id, Protocol.ToElement(data));
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>Serves the catalog, furnace geometry and a one-page factory photograph; any mutation fails the test.</summary>
    private sealed class LogisticsGame(ProductionCatalog catalog, SpatialSnapshot map, IReadOnlyList<FactoryRecord> records) : IGameClient
    {
        private const long SnapshotTick = 100;
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
                    snapshotId = $"s{Calls.Count}", scope = catalog.Scope, snapshotScope = catalog.Scope, collectedTick = SnapshotTick, expiresTick = 1000,
                    totalRecords = records.Count, offset = 0, nextOffset = records.Count, complete = true,
                    coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                    records
                }, SnapshotTick),
                _ => throw new InvalidOperationException($"Unexpected request: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
