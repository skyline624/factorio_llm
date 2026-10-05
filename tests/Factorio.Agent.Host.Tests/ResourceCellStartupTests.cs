using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceCellStartupTests
{
    private static readonly FactoryCell Cell = new("coal-cell", 0, new(1, 0, true), "miner", "burner-mining-drill", "coal",
        new Dictionary<string, string> { ["drill"] = "drill" }, "ready", 1);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(4_000_000, 0, false)]
    public void LoadedOrAlreadyBurningFuelDoesNotTriggerAnotherStartup(double burning, long loaded, bool cold)
    {
        Assert.Equal(cold, ResourceCellStartup.Cold(Snapshot(burning, loaded), "drill"));
    }

    [Fact]
    public void AnElectricProducerDoesNotRequestBurnerFuel()
    {
        var snapshot = Snapshot(0, 0);
        var records = snapshot.Records.Where(r => r.Kind == "entity").Select(r => r with { Data = JsonSerializer.SerializeToElement(new { }) }).ToArray();
        Assert.False(ResourceCellStartup.Cold(snapshot with { Records = records }, "drill"));
    }

    [Fact]
    public void ExistingDifferentFuelIsPreservedRatherThanMixedWithCoal()
    {
        Assert.False(ResourceCellStartup.Cold(Snapshot(0, 1, "wood"), "drill"));
    }

    [Fact]
    public void ADisappearedDrillCannotBeTreatedAsAColdProducer()
    {
        var snapshot = Snapshot(0, 0);
        Assert.Throws<InvalidOperationException>(() => ResourceCellStartup.Cold(snapshot with { Records = [] }, "drill"));
    }

    [Theory]
    [InlineData(50, 50, 50, 12)]
    [InlineData(1, 50, 50, 1)]
    [InlineData(0, 50, 50, 0)]
    [InlineData(50, 0, 50, 0)]
    [InlineData(50, 3, 50, 3)]
    [InlineData(50, 50, 1, 1)]
    public void StarterIsBoundedByCarriedStockNativeCapacityAndAQuarterStack(long carried, long capacity, int stack, int expected)
    {
        Assert.Equal(expected, ResourceCellStartup.StarterCount(carried, capacity, stack));
    }

    [Fact]
    public void SmeltingStartsTheOreSourceBeforeItsFurnaceAndLeavesAssemblersToTheirOwnStartup()
    {
        var smelter = Cell with { Kind = "smelter", Recipe = "copper-plate", Entities = new Dictionary<string, string>
            { ["drill"] = "ore-source", ["furnace"] = "receiver", ["pole"] = "pole", ["output-chest"] = "output" } };
        Assert.Equal(["ore-source", "receiver"], ResourceCellStartup.BurnerEntities(smelter));
        Assert.Equal(["drill"], ResourceCellStartup.BurnerEntities(Cell));
        Assert.Empty(ResourceCellStartup.BurnerEntities(smelter with { Zone = 1, Kind = "assembler" }));
    }

    [Fact]
    public void RetainedRawSuppliersStartEvenWhenTheirReadyCapacityAlreadyCoversThePlan()
    {
        var iron = Cell with { Id = "iron", Kind = "smelter", Recipe = "iron-plate" };
        var unrelated = iron with { Id = "copper", Recipe = "copper-plate" };
        var state = new FactoryState(1, "world", [], [iron, Cell, unrelated, iron with { Id = "open", Status = "building" },
            iron with { Id = "depleted", Status = "depleted" }]);
        var wanted = new Dictionary<string, double> { ["iron-plate"] = 12, ["copper-plate"] = 0 };
        Assert.Equal(["coal-cell", "iron"], FactoryDirector.RawStartupCells(Catalogs.Raw(), state, wanted).Select(c => c.Id));
        Assert.Empty(FactoryDirector.RawStartupCells(Catalogs.Raw(), state, new Dictionary<string, double>()));
    }

    [Fact]
    public async Task ALoadedFleetDoesNotRepeatTheFactoryCensusOrRequestAnyActorWork()
    {
        var cells = Enumerable.Range(0, 24).Select(i => Cell with { Id = $"smelter-{i}", Kind = "smelter",
            Entities = new Dictionary<string, string> { ["drill"] = $"drill-{i}", ["furnace"] = $"furnace-{i}" } }).ToArray();
        var game = new CensusGame(cells.SelectMany(ResourceCellStartup.BurnerEntities).SelectMany(id => Records(id, 1)).ToArray());
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await new ResourceCellStartup(game, journal).StartManyAsync(cells, Catalogs.Raw(), controller, default);

        Assert.Equal(1, game.Photos);
        Assert.Empty(journal.Selected);
        Assert.Equal(24, journal.Inspected);
    }

    [Fact]
    public async Task AProducerThatBecomesLoadedAfterTheCensusIsReobservedWithoutAnotherTransfer()
    {
        var game = new CensusGame(Records("drill", 0), warmAfterCensus: true);
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await new ResourceCellStartup(game, journal).StartManyAsync([Cell], Catalogs.Raw(), controller, default);

        Assert.Equal([Cell.Id], journal.Selected);
        Assert.Equal(2, game.Photos); // The first cold observation is never used as authority to insert.
    }

    [Fact]
    public async Task ResearchCompletingDuringTheCensusPreventsStartingItsSelectedColdProducer()
    {
        var game = new CensusGame(Records("drill", 0));
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);
        int reads = 0;

        await new ResourceCellStartup(game, journal).StartManyAsync([Cell], Catalogs.Raw(), controller, default,
            _ => Task.FromResult(++reads == 2));

        Assert.Equal(1, game.Photos);
        Assert.Equal([Cell.Id], journal.Selected);
    }

    [Fact]
    public async Task ALoadedFleetFromAnotherIncarnationIsRejectedBeforeItCanBeSkipped()
    {
        var game = new CensusGame(Records("drill", 1), changedActor: true);
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ResourceCellStartup(game, journal)
            .StartManyAsync([Cell], Catalogs.Raw(), controller, default));

        Assert.Equal(1, game.Photos);
        Assert.Equal(0, journal.Inspected);
    }

    private static FactoryRecord[] Records(string id, long loaded) =>
        [new(id, "entity", id, "burner-mining-drill", Protocol.ToElement(new { burnerRemainingJoules = 0, fuelInventoryId = id + "-fuel" })),
         new(id + "-fuel", "inventory", id, "fuel", Protocol.ToElement(new { items = new Dictionary<string, long> { ["coal"] = loaded } }))];

    private sealed class ScanJournal : IControllerJournal
    {
        public string[] Selected { get; private set; } = [];
        public int Inspected { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Assert.Equal("resource-startup-scan", type);
            var scan = Protocol.ToElement(data);
            Selected = scan.GetProperty("selectedCells").EnumerateArray().Select(c => c.GetString()!).ToArray();
            Inspected = scan.GetProperty("inspectedCells").GetInt32();
            return Task.CompletedTask;
        }
    }

    private sealed class CensusGame(FactoryRecord[] records, bool warmAfterCensus = false, bool changedActor = false) : IGameClient
    {
        public int Photos { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("factory_snapshot", request.Action); // Any observation for procurement, movement or transfer fails the test.
            Photos++;
            var scope = Catalogs.Raw().Scope;
            if (changedActor) scope = scope with { Incarnation = scope.Incarnation + 1 };
            var current = warmAfterCensus && Photos > 1 ? records.Select(r => r.Kind == "inventory"
                ? r with { Data = Protocol.ToElement(new { items = new Dictionary<string, long> { ["coal"] = 1L } }) } : r).ToArray() : records;
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100 + Photos, Protocol.ToElement(new
            {
                snapshotId = "census-" + Photos, scope, snapshotScope = scope, collectedTick = 100 + Photos, expiresTick = 3700 + Photos,
                totalRecords = current.Length, offset = 0, nextOffset = current.Length, complete = true,
                coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                records = current
            })));
        }
    }

    private static FactorySnapshot Snapshot(double burning, long loaded, string fuel = "coal") => new("fixture",
        ConstructionSupplyPlannerTests.Catalog().Scope, 1, 100, JsonSerializer.SerializeToElement(new { }),
        [new("drill", "entity", "drill", "burner-mining-drill", JsonSerializer.SerializeToElement(new { burnerRemainingJoules = burning, fuelInventoryId = "fuel" })),
         new("fuel", "inventory", "drill", "fuel", JsonSerializer.SerializeToElement(new { items = new Dictionary<string, long> { [fuel] = loaded } }))]);
}
