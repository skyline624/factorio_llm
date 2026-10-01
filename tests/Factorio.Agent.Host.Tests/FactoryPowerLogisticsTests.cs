using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryPowerLogisticsTests
{
    [Theory]
    [InlineData(20, 0, 30)]
    [InlineData(0, 0, 50)]
    [InlineData(60, 0, 0)]
    [InlineData(5, 1, 0)]
    public void FeederChestsAreFilledWithFuelToOneStackButNeverMixed(long coal, long wood, long need)
    {
        var chest = new Dictionary<string, long>(StringComparer.Ordinal) { ["coal"] = coal };
        if (wood > 0) chest["wood"] = wood;
        Assert.Equal(need, FactoryLogistics.PowerFuelNeed(chest, "coal", 50));
    }

    [Fact]
    public void NewCellDemandCountsTheMachineAndItsTwoInsertersFromNativeUsage()
    {
        var map = FactoryMaps.Grass(4);
        var equipment = new Core.CellEquipment("assembling-machine-1", "inserter", "iron-chest", "small-electric-pole");
        Assert.Equal(1250 + 2 * 245, PowerExpansionController.CellDemand(map, equipment, io: true), 6);
        Assert.Equal(1000, PowerExpansionController.CellDemand(map, equipment with { Machine = "lab" }, io: false), 6);
    }

    [Theory]
    [InlineData(true, 3, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 11, true)]
    [InlineData(false, 12, false)]
    public void BoilersWithAFeederCellAreOnlyRestartedWhenDry(bool fedByCell, long loaded, bool direct)
    {
        Assert.Equal(direct, FactoryLogistics.NeedsDirectFuel(fedByCell, loaded, 50));
    }

    [Theory]
    [InlineData(45, 3, 0)]
    [InlineData(8, 4, 0)]
    [InlineData(5, 2, 195)]
    [InlineData(0, 1, 200)]
    public void OnlyALowFeederSupplyAsksForCoalProcurement(long chest, long boiler, long shortfall)
    {
        using var world = new World(chest, boiler, carried: 0);
        var result = world.Service();
        Assert.Equal(shortfall, result.Shortfall.GetValueOrDefault("coal"));
        Assert.DoesNotContain("submit", world.Game.Calls);
    }

    [Fact]
    public void ADestroyedFeederChestIsSkippedWithoutStoppingLogistics()
    {
        using var world = new World(chest: null, boiler: 20, carried: 30);
        var result = world.Service();
        Assert.Empty(result.Shortfall);
        Assert.DoesNotContain("submit", world.Game.Calls);
        Assert.Contains("factory-cell-missing", world.Journal.Types);
    }

    /// <summary>One registered power cell and the native photograph of its boiler, chest (absent when null) and inserter.</summary>
    private sealed class World : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"factory-power-logistics-{Guid.NewGuid():N}");
        public LogisticsGame Game { get; }
        public Journal Journal { get; } = new();

        public World(long? chest, long boiler, long carried)
        {
            Directory.CreateDirectory(directory);
            var cell = new FactoryCell("power-b1", PowerExpansionController.PowerZone, new(0, 0, true), "power", "boiler", null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["boiler"] = "b1", ["input-chest"] = "c1", ["input-inserter"] = "i1" },
                "ready", 1);
            new FactoryRegistry(directory).SaveAsync(new(1, LogisticsGame.Scope.WorldId, [], [cell]), CancellationToken.None).GetAwaiter().GetResult();
            var records = new List<FactoryRecord>
            {
                Record("actor", "entity", "actor", new { role = "actor", type = "character", position = new MapPosition(0, 0), mainInventoryId = "actor-main" }),
                Record("actor-main", "inventory", "actor", new { items = new Dictionary<string, long> { ["coal"] = carried } }),
                Record("b1", "entity", "b1", new { role = "factory", type = "boiler", position = new MapPosition(1, 2.5), fuelInventoryId = "b1-fuel" }),
                Record("b1-fuel", "inventory", "b1", new { items = new Dictionary<string, long> { ["coal"] = boiler } }),
                Record("i1", "entity", "i1", new { role = "factory", type = "inserter", position = new MapPosition(-1.5, 2.5) })
            };
            if (chest is { } coal)
            {
                records.Add(Record("c1", "entity", "c1", new { role = "factory", type = "container", position = new MapPosition(-2.5, 2.5) }));
                records.Add(Record("c1-chest", "inventory", "c1", new { items = new Dictionary<string, long> { ["coal"] = coal } }));
            }
            Game = new(records);
        }

        public LogisticsResult Service() => new FactoryLogistics(Game, Journal, directory).ServiceAsync().GetAwaiter().GetResult();

        public void Dispose() => Directory.Delete(directory, true);

        private static FactoryRecord Record(string id, string kind, string entityId, object data) => new(id, kind, entityId, id, Protocol.ToElement(data));
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

    /// <summary>Serves the catalog and a one-page factory photograph; any mutation fails the test.</summary>
    private sealed class LogisticsGame(IReadOnlyList<FactoryRecord> records) : IGameClient
    {
        public static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
        public List<string> Calls { get; } = [];

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "production_catalog" => new ProductionCatalog(Scope, 10, [], new Dictionary<string, NativeItem> { ["coal"] = new(4e6, 50, "chemical") },
                    new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>()),
                "factory_snapshot" => new
                {
                    snapshotId = $"s{Calls.Count}", scope = Scope, snapshotScope = Scope, collectedTick = 10, expiresTick = 100,
                    totalRecords = records.Count, offset = 0, nextOffset = records.Count, complete = true,
                    coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                    records
                },
                _ => throw new InvalidOperationException($"Unexpected request: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 10, Protocol.ToElement(data)));
        }
    }
}
