using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

/// <summary>Launch-time upkeep of a registered silo cell: cadence, faults, stalls, procurement choices and the one-cell rule.</summary>
public sealed class SiloCellSupplyTests
{
    private static readonly NativeRecipe Part = SiloCatalogs.Rocket().Recipes.Single(r => r.Name == "rocket-part");

    [Fact]
    public async Task AStockedCellWhoseInserterIsDestroyedIsMaintainedAtOnceThenStopsNamingIt()
    {
        // Fifty parts to go and five in the chest: nothing to procure, so only the destroyed inserter explains an idle silo.
        using var world = new SiloWorld(arm: true);
        var supply = world.Supply();
        var silo = Silo(parts: 50);
        Assert.True(await world.TendAsync(supply, silo));
        Assert.Equal(1, world.Journal.Count("factory-logistics"));
        world.Game.Records = SiloWorld.Records(arm: false, network: 1);
        for (int observation = 1; observation < SiloCellSupply.StallObservations; observation++)
            Assert.False(await world.TendAsync(supply, silo));
        // Maintenance ran at the first sight of the fault, long before the cadence, and only then while the actor lacks a spare.
        Assert.Equal(2, world.Journal.Count("factory-logistics"));
        Assert.Equal(1, world.Journal.Count("factory-rebuild-shortfall"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => world.TendAsync(supply, silo));
        Assert.Contains("input-inserter missing", error.Message);
        // One last maintenance round before giving up, then a reconcile error instead of the two-hour budget.
        Assert.Equal(3, world.Journal.Count("factory-logistics"));
        Assert.DoesNotContain("submit", world.Game.Calls);
    }

    [Fact]
    public async Task AWorkingCellIsServicedOnItsCadenceAndNeverStalls()
    {
        using var world = new SiloWorld(arm: true);
        var supply = world.Supply();
        int observations = SiloCellSupply.StallObservations + 20;
        for (int observation = 0; observation < observations; observation++)
        {
            // The inserter keeps loading the silo between observations sixty ticks apart.
            Assert.True(await world.TendAsync(supply, Silo(parts: 50, loaded: observation % 2)));
            world.Game.Tick += 60;
        }
        // The stocked chest needs nothing, yet logistics still runs once a minute for the rest of the factory.
        long rounds = 1 + (observations - 1) * 60 / SiloCellSupply.ServiceIntervalTicks;
        Assert.Equal(rounds, world.Journal.Count("factory-logistics"));
        Assert.DoesNotContain("submit", world.Game.Calls);
    }

    [Fact]
    public async Task AHealthyCellWaitingForTheEngineNeverStalls()
    {
        // Every part made: the engine builds the rocket, and nothing the cell holds has to move meanwhile.
        using var world = new SiloWorld(arm: true);
        var supply = world.Supply();
        for (int observation = 0; observation < SiloCellSupply.StallObservations + 5; observation++)
            Assert.True(await world.TendAsync(supply, Silo(parts: 100)));
        Assert.Equal(1, world.Journal.Count("factory-logistics"));
    }

    [Fact]
    public async Task AnUnpoweredCellIsMaintainedOnceThenStopsNamingItsRoles()
    {
        using var world = new SiloWorld(arm: true, network: null);
        var supply = world.Supply();
        for (int observation = 0; observation < SiloCellSupply.StallObservations; observation++)
            await world.TendAsync(supply, Silo(parts: 50));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => world.TendAsync(supply, Silo(parts: 50)));
        Assert.Contains("input-inserter unpowered", error.Message);
        Assert.Contains("machine unpowered", error.Message);
        Assert.Equal(2, world.Journal.Count("factory-logistics"));
    }

    [Fact]
    public async Task StructuresMissingFromTheChestWaitForTheirCellsWithoutHandCraftingOrExtraRounds()
    {
        // No structure left in the chest, but a ready assembler cell makes them: logistics collects its output on its cadence.
        var structures = new FactoryCell("structures", 1, new(0, 0, true), "assembler", "assembling-machine-2", "low-density-structure",
            new Dictionary<string, string>(), "ready", 1);
        using var world = new SiloWorld(arm: true, structures: 0, extra: structures);
        var supply = world.Supply();
        for (int observation = 0; observation < 10; observation++)
        {
            Assert.True(await world.TendAsync(supply, Silo(parts: 50)));
            world.Game.Tick += 60;
        }
        Assert.Equal(1, world.Journal.Count("factory-logistics"));
        var round = world.Journal.Data("silo-cell-supply").Single();
        Assert.Equal(["low-density-structure"], round.GetProperty("leftToCells").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(round.GetProperty("craft").EnumerateArray());
        // Nested production, which would observe the actor first, never started.
        Assert.DoesNotContain("observe", world.Game.Calls);
        Assert.DoesNotContain("submit", world.Game.Calls);
    }

    [Fact]
    public void CellFaultsNameDestroyedRolesAndRolesCutFromEveryGenerator()
    {
        Assert.Empty(SiloCellSupply.Faults(Snapshot(SiloWorld.Records(arm: true, network: 1)), Cell()));
        Assert.Equal(["input-inserter missing"], SiloCellSupply.Faults(Snapshot(SiloWorld.Records(arm: false, network: 1)), Cell()));
        Assert.Equal(["input-inserter unpowered", "machine unpowered", "pole unpowered"],
            SiloCellSupply.Faults(Snapshot(SiloWorld.Records(arm: true, network: null)), Cell()));
    }

    [Fact]
    public void OnlyIngredientsNoReadyCellMakesAreCraftedForTheSilo()
    {
        var catalog = SiloCatalogs.Rocket();
        var structures = new FactoryCell("structures", 1, new(0, 0, true), "assembler", "assembling-machine-2", "low-density-structure",
            new Dictionary<string, string> { ["machine"] = "a1" }, "ready", 1);
        var state = new FactoryState(1, catalog.Scope.WorldId, [], [Cell(), structures]);
        // Structures wait for their cells, whose output logistics collects; processing units and rocket fuel have none.
        Assert.True(SiloCellSupply.MadeByCells(state, catalog, "low-density-structure"));
        Assert.False(SiloCellSupply.MadeByCells(state, catalog, "processing-unit"));
        Assert.False(SiloCellSupply.MadeByCells(state with { Cells = [Cell(), structures with { Status = "building" }] }, catalog, "low-density-structure"));
        // Resource cells record their product as their recipe.
        var miner = new FactoryCell("coal", 0, new(1, 0, true), "miner", "electric-mining-drill", "coal", new Dictionary<string, string>(), "ready", 1);
        Assert.True(SiloCellSupply.MadeByCells(state with { Cells = [miner] }, catalog, "coal"));
    }

    [Fact]
    public void AFactoryRegistersOneSiloCellAndALaunchRestoresIt()
    {
        var ready = new FactoryState(1, "world", [], [Cell()]);
        // A building cell or a ready one whose silo is gone still occupies the factory's single silo cell.
        Assert.NotNull(FactoryCellBuilder.Refusal(ready, SiloCellPlanner.Kind));
        Assert.NotNull(FactoryCellBuilder.Refusal(ready with { Cells = [Cell() with { Status = "building" }] }, SiloCellPlanner.Kind));
        Assert.Null(FactoryCellBuilder.Refusal(new FactoryState(1, "world", [], []), SiloCellPlanner.Kind));
        Assert.Null(FactoryCellBuilder.Refusal(ready, "assembler"));
        Assert.Equal("cell-1", SiloCellSupply.Owned(ready, "rocket-silo")?.Id);
        Assert.Equal("cell-1", SiloCellSupply.Owned(ready with { Cells = [Cell() with { Status = "building" }] }, "rocket-silo")?.Id);
        Assert.Null(SiloCellSupply.Owned(ready, "another-silo"));
    }

    [Fact]
    public async Task ACellWithoutASiloPlanStopsTheLaunchInsteadOfGrowingASecondCell()
    {
        using var world = new SiloWorld(arm: true);
        var state = new FactoryState(1, world.Catalog.Scope.WorldId, [], [Cell() with { Plan = null }]);
        await using var controller = new SpatialController(world.Game, world.Journal);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Supply().RestoreAsync(state, "rocket-silo", SiloCatalogs.Silo, world.Catalog, controller, CancellationToken.None));
        Assert.Contains("cell-1", error.Message);
        Assert.Empty(world.Game.Calls);
    }

    private static ObservedRocketSilo Silo(int parts, long loaded = 0) => new("silo", "rocket-silo", new(4.5, -4.5), "rocket-part", parts,
        "building_rocket", false, false, 1000, new Dictionary<string, long> { ["processing-unit"] = loaded },
        new Dictionary<string, long> { ["processing-unit"] = 20, ["low-density-structure"] = 20, ["rocket-fuel"] = 20 }, 1);

    private static FactoryCell Cell() => new("cell-1", 2, new(0, 0, true), SiloCellPlanner.Kind, "rocket-silo", "rocket-part",
        new Dictionary<string, string> { ["machine"] = "silo", ["input-inserter"] = "arm", ["input-chest"] = "chest", ["pole"] = "pole" }, "ready", 1,
        Plan: new Dictionary<string, PlannedEntity>
        {
            ["machine"] = new("machine", "rocket-silo", new(4.5, -4.5), 0), ["input-inserter"] = new("input-inserter", "inserter", new(0.5, 2.5), 8),
            ["input-chest"] = new("input-chest", "iron-chest", new(0.5, 3.5), 0), ["pole"] = new("pole", "small-electric-pole", new(1.5, 2.5), 0)
        });

    private static FactorySnapshot Snapshot(IReadOnlyList<FactoryRecord> records) =>
        new("snapshot", SiloCatalogs.Rocket().Scope, 1, 2, Protocol.ToElement(new { }), records);

    /// <summary>A registered ready silo cell, its chest holding five parts' ingredients, and the native photograph of its parts.</summary>
    private sealed class SiloWorld : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"silo-cell-supply-{Guid.NewGuid():N}");
        public ProductionCatalog Catalog { get; } = SiloCatalogs.Rocket();
        public SiloGame Game { get; }
        public Journal Journal { get; } = new();

        public SiloWorld(bool arm, long? network = 1, long structures = 50, FactoryCell? extra = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            new FactoryRegistry(Directory).SaveAsync(new(1, Catalog.Scope.WorldId, [], extra is null ? [Cell()] : [Cell(), extra]), CancellationToken.None)
                .GetAwaiter().GetResult();
            Game = new(Catalog, Records(arm, network, structures));
        }

        public SiloCellSupply Supply() => new(Game, Journal, Directory);

        public Task<bool> TendAsync(SiloCellSupply supply, ObservedRocketSilo silo) =>
            supply.TendAsync("cell-1", SiloCatalogs.Silo, Part, Catalog, () => Task.FromResult(silo), CancellationToken.None);

        /// <summary>Actor without a spare inserter, silo, chest, pole and a generator; the inserter only when it stands.</summary>
        public static IReadOnlyList<FactoryRecord> Records(bool arm, long? network, long structures = 50)
        {
            var records = new List<FactoryRecord>
            {
                Record("actor", "entity", "actor", new { role = "actor", type = "character", position = new MapPosition(0, 0), mainInventoryId = "actor-main" }),
                Record("actor-main", "inventory", "actor", new { items = new Dictionary<string, long>() }),
                Record("silo", "entity", "silo", new { role = "factory", type = "rocket-silo", position = new MapPosition(4.5, -4.5), direction = 0, power = new { networkId = network } }),
                Record("chest", "entity", "chest", new { role = "factory", type = "container", position = new MapPosition(0.5, 3.5), direction = 0 }),
                Record("chest-inventory", "inventory", "chest", new
                {
                    items = new Dictionary<string, long> { ["processing-unit"] = 50, ["low-density-structure"] = structures, ["rocket-fuel"] = 50 }
                }),
                Record("pole", "entity", "pole", new { role = "factory", type = "electric-pole", position = new MapPosition(1.5, 2.5), direction = 0, power = new { networkId = network } }),
                Record("engine", "entity", "engine", new { role = "factory", type = "generator", position = new MapPosition(30, 0), direction = 0, power = new { networkId = 1 } })
            };
            if (arm)
                records.Add(Record("arm", "entity", "arm", new { role = "factory", type = "inserter", position = new MapPosition(0.5, 2.5), direction = 8, power = new { networkId = network } }));
            return records;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, true);

        private static FactoryRecord Record(string id, string kind, string entityId, object data) => new(id, kind, entityId, id, Protocol.ToElement(data));
    }

    private sealed class Journal : IControllerJournal
    {
        private readonly List<(string Type, JsonElement Data)> rows = [];
        public int Count(string type) => rows.Count(r => r.Type == type);
        public IEnumerable<JsonElement> Data(string type) => rows.Where(r => r.Type == type).Select(r => r.Data);
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            rows.Add((type, Protocol.ToElement(data)));
            return Task.CompletedTask;
        }
    }

    /// <summary>Serves the rocket catalog and a one-page factory photograph at the current tick; any mutation fails the test.</summary>
    private sealed class SiloGame(ProductionCatalog catalog, IReadOnlyList<FactoryRecord> records) : IGameClient
    {
        public long Tick { get; set; } = 100;
        public IReadOnlyList<FactoryRecord> Records { get; set; } = records;
        public List<string> Calls { get; } = [];

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "production_catalog" => catalog with { CollectedTick = Tick },
                "factory_snapshot" => new
                {
                    snapshotId = $"s{Calls.Count}", scope = catalog.Scope, snapshotScope = catalog.Scope, collectedTick = Tick, expiresTick = Tick + 1000,
                    totalRecords = Records.Count, offset = 0, nextOffset = Records.Count, complete = true,
                    coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                    records = Records
                },
                _ => throw new InvalidOperationException($"Unexpected request: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Tick, Protocol.ToElement(data)));
        }
    }
}
