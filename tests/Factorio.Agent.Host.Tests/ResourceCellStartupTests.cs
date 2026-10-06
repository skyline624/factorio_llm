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
    public async Task AnAbsentRetainedCellDoesNotBlockInspectionOfStandingProducers()
    {
        var live = Cell with { Id = "live", Entities = new Dictionary<string, string> { ["drill"] = "live-drill" } };
        var game = new CensusGame(Records("live-drill", 1));
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await new ResourceCellStartup(game, journal).StartManyAsync([Cell, live], Catalogs.Raw(), controller, default);

        Assert.Equal(1, game.Photos);
        Assert.Empty(journal.Selected);
        Assert.Equal([Cell.Id], journal.Missing);
        Assert.Equal(2, journal.Inspected);
    }

    [Fact]
    public async Task DisappearanceAfterTheCensusDefersStartupBeforeAnyActorWork()
    {
        var game = new CensusGame(Records("drill", 0), disappearAfterCensus: true);
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await new ResourceCellStartup(game, journal).StartManyAsync([Cell], Catalogs.Raw(), controller, default);

        Assert.Equal([Cell.Id], journal.Selected);
        Assert.Equal(["drill"], journal.Deferred);
        Assert.Equal(2, game.Photos);
    }

    [Fact]
    public async Task AnExplicitFreshCensusIsUsedOnlyForSelectionAndNotForFuelInsertion()
    {
        var game = new CensusGame(Records("drill", 1));
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);

        await new ResourceCellStartup(game, journal).StartManyAsync([Cell], Catalogs.Raw(), controller, default,
            census: Snapshot(0, 0) with { Scope = Catalogs.Raw().Scope });

        Assert.Equal([Cell.Id], journal.Selected);
        Assert.Equal(1, game.Photos);
        Assert.Empty(journal.Deferred);
    }

    [Fact]
    public async Task AnExplicitCensusFromAnotherActorIsRejectedBeforeSelection()
    {
        var game = new CensusGame([]);
        var journal = new ScanJournal();
        await using var controller = new SpatialController(game, journal);
        var scope = Catalogs.Raw().Scope;

        await Assert.ThrowsAsync<InvalidDataException>(() => new ResourceCellStartup(game, journal)
            .StartManyAsync([Cell], Catalogs.Raw(), controller, default,
                census: Snapshot(0, 0) with { Scope = scope with { Generation = scope.Generation + 1 } }));

        Assert.Equal(0, game.Photos);
        Assert.Equal(0, journal.Inspected);
    }

    [Theory]
    [InlineData("missing-drill", "building", 1)]
    [InlineData("missing-chest", "building", 1)]
    [InlineData("depleted", "depleted", 2)]
    [InlineData("no-power", "ready", 2)]
    public async Task NativeHealthPersistsBeforeStartupAndRemovesLostCapacity(string condition, string status, int retained)
    {
        string directory = Directory.CreateTempSubdirectory("raw-census-").FullName;
        try
        {
            var catalog = Catalogs.Raw();
            var row = new ResourceRow(1, "miner", "coal", "coal", new("electric-mining-drill", "iron-chest", Pole: "small-electric-pole"),
                new(0, 0), 0, 3, 1, 30);
            var cell = Cell with { Slot = new(1, 0, true), Attempts = 2,
                Entities = new Dictionary<string, string> { ["drill"] = "drill", ["output-chest"] = "chest" } };
            var registry = new FactoryRegistry(directory);
            await registry.SaveAsync(new(1, catalog.Scope.WorldId, [], [cell], [row]), default);
            var records = new List<FactoryRecord>();
            foreach (string id in new[] { "drill", "chest" }.Where(id => condition != "missing-" + id))
                records.Add(new(id, "entity", id, id, Protocol.ToElement(new { role = "factory" })));
            records.Add(new("work", "work", "drill", "drill", Protocol.ToElement(new
                { statusName = condition == "depleted" ? "no_minable_resources" : "no_power" })));
            var snapshot = Snapshot(0, 0) with { Scope = catalog.Scope, Records = records.ToArray() };
            var journal = new HealthJournal();
            var director = new FactoryDirector(new CensusGame([]), journal, directory);

            var state = await director.ReconcileRawCellsAsync(catalog, snapshot, default);

            var changed = Assert.Single(state.Cells);
            Assert.Equal(status, changed.Status);
            Assert.Equal(retained, changed.Entities.Count);
            Assert.Equal(status == "building" ? 0 : 2, changed.Attempts);
            Assert.Equal(status == "ready" ? (1, 30.0) : (0, 0.0), FactoryDirector.RawCapacity(state, "coal"));
            Assert.Equal(status == "ready" ? 1 : 0, FactoryDirector.RawStartupCells(catalog, state,
                new Dictionary<string, double> { ["coal"] = 30 }).Count);
            Assert.Equal(status, Assert.Single((await registry.LoadAsync(catalog.Scope.WorldId, default)).Cells).Status);
            Assert.Equal(status == "ready" ? 0 : 1, journal.Changes);
            await director.ReconcileRawCellsAsync(catalog, snapshot, default);
            Assert.Equal(status == "ready" ? 0 : 1, journal.Changes); // A fresh unchanged photo does not reopen/reset attempts again.
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ChangedActorCannotInvalidateTheRetainedRegistry()
    {
        string directory = Directory.CreateTempSubdirectory("raw-scope-").FullName;
        try
        {
            var catalog = Catalogs.Raw();
            var registry = new FactoryRegistry(directory);
            await registry.SaveAsync(new(1, catalog.Scope.WorldId, [], [Cell]), default);
            string before = await File.ReadAllTextAsync(registry.Path);
            var snapshot = Snapshot(0, 0) with { Scope = catalog.Scope with { Incarnation = catalog.Scope.Incarnation + 1 }, Records = [] };
            var journal = new HealthJournal();

            await Assert.ThrowsAsync<InvalidDataException>(() => new FactoryDirector(new CensusGame([]), journal, directory)
                .ReconcileRawCellsAsync(catalog, snapshot, default));

            Assert.Equal(before, await File.ReadAllTextAsync(registry.Path));
            Assert.Equal(0, journal.Changes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(601, true)]
    public void RetainedCopperStartupWaitsOnlyWithTenMinutesOfTheCompleteDemand(long copper, bool deferred)
    {
        // Normal seed20261072: a cold old copper furnace blocked chemical science despite 52,000 buffered plates.
        var state = new FactoryState(1, "world", [], [Cell with { Id = "copper", Kind = "smelter", Recipe = "copper-plate" }]);
        var items = FactoryDirector.DeferredRawStartupItems(Catalogs.Raw(), state,
            new Dictionary<string, double> { ["copper-plate"] = 60 },
            new Dictionary<string, long> { ["copper-plate"] = copper }, "chemical-science-pack", 0);
        Assert.Equal(deferred, items.Contains("copper-plate"));
    }

    [Theory]
    [InlineData("chemical-science-pack", true)]
    [InlineData("copper-plate", false)]
    public void AnExplicitRawRateStillStartsItsRetainedSuppliersEvenWithBufferedStock(string priority, bool deferred)
    {
        var state = new FactoryState(1, "world", [], [Cell with { Kind = "smelter", Recipe = "copper-plate" }])
            .WithTarget("copper-plate", 60).WithTarget("chemical-science-pack", 30);
        var items = FactoryDirector.DeferredRawStartupItems(Catalogs.Raw(), state,
            new Dictionary<string, double> { ["copper-plate"] = 60 },
            new Dictionary<string, long> { ["copper-plate"] = 52_000 }, priority, 0);
        Assert.Equal(deferred, items.Contains("copper-plate"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(135, false)]
    public void BoilerFuelCapacityStartsBeforeWorkDespiteBufferedCoal(double powerFuel, bool deferred)
    {
        var state = new FactoryState(1, "world", [], [Cell]);
        var items = FactoryDirector.DeferredRawStartupItems(Catalogs.Raw(), state,
            new Dictionary<string, double> { ["coal"] = 60 + powerFuel },
            new Dictionary<string, long> { ["coal"] = 14_000 }, "chemical-science-pack", powerFuel);
        Assert.Equal(deferred, items.Contains("coal"));
    }

    [Fact]
    public void ImplicitSmelterFuelCannotBeDeferredWhenNoCoalWasObserved()
    {
        var state = new FactoryState(1, "world", [], [Cell, Cell with { Kind = "smelter", Recipe = "iron-plate" }]);
        var items = FactoryDirector.DeferredRawStartupItems(Catalogs.Raw(), state,
            new Dictionary<string, double> { ["iron-plate"] = 12, ["coal"] = 0 },
            new Dictionary<string, long> { ["iron-plate"] = 500 }, "chemical-science-pack", 0);
        Assert.Contains("iron-plate", items);
        Assert.DoesNotContain("coal", items);
    }

    [Theory]
    [InlineData(599, "chest", true, 36)]
    [InlineData(600, "chest", true, 24)]
    [InlineData(600, "corpse", true, 36)]
    [InlineData(52_000, "chest", false, 36)]
    public void StageFuelProcurementExcludesBufferedCopperButKeepsCoalAndBoilerIgnition(long copper, string inventory,
        bool planned, long expected)
    {
        FactoryCell[] cells = [Cell, Cell with { Id = "copper", Kind = "smelter", Recipe = "copper-plate",
            Entities = new Dictionary<string, string> { ["furnace"] = "furnace" } },
            Cell with { Id = "power", Kind = "power", Entities = new Dictionary<string, string> { ["boiler"] = "boiler" } }];
        var records = new List<FactoryRecord>
        {
            new("buffer", "inventory", "storage", inventory, Protocol.ToElement(new { items = new Dictionary<string, long>
                { ["copper-plate"] = copper, ["coal"] = 14_000 } }))
        };
        foreach (string id in new[] { "drill", "furnace", "boiler" })
        {
            records.Add(new(id, "entity", id, id, Protocol.ToElement(new { role = "factory", type = id,
                fuelInventoryId = id + "-fuel" })));
            records.Add(new(id + "-fuel", "inventory", id, "fuel", Protocol.ToElement(new { items = new Dictionary<string, long>() })));
        }
        var snapshot = Snapshot(0, 0) with { Records = records.ToArray() };
        var demand = planned ? new Dictionary<string, double> { ["copper-plate"] = 60, ["coal"] = 195 } : null;
        Assert.Equal(expected, FactoryLogistics.FuelReserve(snapshot, FactoryLogistics.FuelCells(snapshot, cells, demand), 50));
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
        public string[] Missing { get; private set; } = [];
        public List<string> Deferred { get; } = [];
        public int Inspected { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            var scan = Protocol.ToElement(data);
            if (type == "resource-startup-deferred")
            {
                Assert.Equal("native-entity-missing-before-transfer", scan.GetProperty("reason").GetString());
                Deferred.Add(scan.GetProperty("entityId").GetString()!);
                return Task.CompletedTask;
            }
            Assert.Equal("resource-startup-scan", type);
            Selected = scan.GetProperty("selectedCells").EnumerateArray().Select(c => c.GetString()!).ToArray();
            Inspected = scan.GetProperty("inspectedCells").GetInt32();
            Missing = scan.GetProperty("missingCells").EnumerateArray().Select(c => c.GetProperty("cell").GetString()!).ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class HealthJournal : IControllerJournal
    {
        public int Changes { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Assert.Equal("resource-cell-health", type);
            Changes += ((IReadOnlyList<FactoryCell>)data).Count;
            return Task.CompletedTask;
        }
    }

    private sealed class CensusGame(FactoryRecord[] records, bool warmAfterCensus = false, bool changedActor = false,
        bool disappearAfterCensus = false) : IGameClient
    {
        public int Photos { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("factory_snapshot", request.Action); // Any observation for procurement, movement or transfer fails the test.
            Photos++;
            var scope = Catalogs.Raw().Scope;
            if (changedActor) scope = scope with { Incarnation = scope.Incarnation + 1 };
            var current = disappearAfterCensus && Photos > 1 ? [] : warmAfterCensus && Photos > 1 ? records.Select(r => r.Kind == "inventory"
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
