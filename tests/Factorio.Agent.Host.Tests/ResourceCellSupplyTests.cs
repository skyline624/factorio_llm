using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceCellSupplyTests
{
    private static readonly ResourceCellEquipment Smelter = new("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
    private static readonly ResourceRow IronRow = new(1, "smelter", "iron-plate", "iron-ore", Smelter, new(10, -4), 0, 3, 3, 18.75);
    private static readonly ResourceRow CoalRow = new(2, "miner", "coal", "coal", new("electric-mining-drill", "iron-chest", Pole: "small-electric-pole"),
        new(-20, 8), 4, 3, 1, 30);

    [Fact]
    public async Task RegistryKeepsResourceRowsAndReadsFilesWrittenBeforeRowsExisted()
    {
        string directory = Directory.CreateTempSubdirectory("factory-rows-").FullName;
        try
        {
            var registry = new FactoryRegistry(directory);
            var state = (await registry.LoadAsync("world", CancellationToken.None)).With(IronRow).With(IronRow with { Cells = 2 });
            state = state.With(Cell(IronRow, 0, "ready"));
            await registry.SaveAsync(state, CancellationToken.None);
            var loaded = await registry.LoadAsync("world", CancellationToken.None);
            Assert.Equal(IronRow with { Cells = 2 }, Assert.Single(loaded.Rows!));
            Assert.Single(loaded.Cells);
            await File.WriteAllTextAsync(registry.Path, """{"version":1,"worldId":"world","zones":[],"cells":[]}""");
            Assert.Empty((await registry.LoadAsync("world", CancellationToken.None)).Rows ?? []);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RawCapacityCountsOnlyReadyCellsOfRowsProducingTheItem()
    {
        var state = new FactoryState(1, "world", [], [Cell(IronRow, 0, "ready"), Cell(IronRow, 1, "ready"), Cell(IronRow, 2, "building"),
            Cell(CoalRow, 0, "ready"), new("gears", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string>(), "ready", 1)], [IronRow, CoalRow]);
        Assert.Equal((2, 37.5), FactoryDirector.RawCapacity(state, "iron-plate"));
        Assert.Equal((1, 30.0), FactoryDirector.RawCapacity(state, "coal"));
        Assert.Equal((0, 0.0), FactoryDirector.RawCapacity(state, "copper-plate"));
        Assert.Equal((0, 0.0), FactoryDirector.RawCapacity(state with { Rows = null }, "iron-plate"));
    }

    [Fact]
    public void GrowthNeedsAShortfallPersistingOverGameTimeAndCapacityBelowThePlannedRate()
    {
        var growth = new RawCapacityGrowth(persistentTicks: 3600, persistentRounds: 2, maximumCells: 4);
        growth.Observe(Round(1000, ["iron-plate"]));
        growth.Observe(Round(1300, ["iron-plate"]));
        Assert.False(growth.Due("iron-plate", cells: 2, capacity: 18.75, demand: 30)); // Two rounds seconds apart.
        growth.Observe(Round(4600, ["iron-plate"]));
        Assert.True(growth.Due("iron-plate", 2, 18.75, 30));
        // Cells that already cover the plan are never grown, however long a chest buffer stays short.
        Assert.False(growth.Due("iron-plate", 2, 37.5, 30));
        Assert.False(growth.Due("iron-plate", 4, 18.75, 30)); // The item's cell budget is spent.
        // A round without shortfall ends the streak instead of letting scattered rounds accumulate.
        growth.Observe(Round(5000, []));
        growth.Observe(Round(9000, ["iron-plate"]));
        Assert.False(growth.Due("iron-plate", 2, 18.75, 30));
        growth.Observe(Round(12600, ["iron-plate"]));
        Assert.True(growth.Due("iron-plate", 2, 18.75, 30));
        growth.Grew("iron-plate");
        growth.Observe(Round(12700, ["iron-plate"]));
        Assert.False(growth.Due("iron-plate", 3, 18.75, 30)); // The new cell delivers before another streak counts.
        growth.Failed("iron-plate");
        growth.Observe(Round(20000, ["iron-plate"]));
        Assert.True(growth.HasFailed("iron-plate"));
        Assert.False(growth.Due("iron-plate", 3, 18.75, 30));
    }

    [Fact]
    public void UnplannedFuelGrowsPastItsDefaultRateOnlyWhileReadyCellsDeliverTheirCapacity()
    {
        var silent = new RawCapacityGrowth();
        Assert.Equal(30, silent.Demand("iron-plate", planned: 30, capacity: 37.5));
        Assert.Equal(RawCapacityGrowth.DefaultPerMinute, silent.Demand("coal", 0, 0));
        // An unpowered miner delivers nothing: more miners on the same starved network would not help.
        silent.Observe(Round(0, ["coal"]));
        silent.Observe(Round(3600, ["coal"]));
        Assert.Equal(RawCapacityGrowth.DefaultPerMinute, silent.Demand("coal", 0, 30));
        Assert.False(silent.Due("coal", 1, 30, silent.Demand("coal", 0, 30)));
        // The same miner delivering its 30 coal a minute while burners still starve earns one more default step.
        var saturated = new RawCapacityGrowth();
        saturated.Observe(Round(0, ["coal"], coal: 40)); // Collected before the streak began: not a rate over it.
        saturated.Observe(Round(3600, ["coal"], coal: 30));
        Assert.Equal(30, saturated.Delivered("coal"), 6);
        Assert.Equal(30 + RawCapacityGrowth.DefaultPerMinute, saturated.Demand("coal", 0, 30));
        Assert.True(saturated.Due("coal", 1, 30, saturated.Demand("coal", 0, 30)));
    }

    [Fact]
    public void CellBudgetCountsStandingAndUnfinishedCellsAcrossRuns()
    {
        var state = new FactoryState(1, "world", [], [Cell(IronRow, 0, "ready"), Cell(IronRow, 1, "building"), Cell(IronRow, 2, "depleted"),
            Cell(IronRow, 3, "abandoned"), Cell(CoalRow, 0, "ready")], [IronRow with { Cells = 4 }, CoalRow]);
        Assert.Equal(2, RawCapacityGrowth.Cells(state, "iron-plate"));
        Assert.Equal(1, RawCapacityGrowth.Cells(state, "coal"));
    }

    [Fact]
    public void ActorProcuresRawItemsWhoseCellsFellSilentWhoseGrowthFailedOrThatStarvePower()
    {
        var delivery = new CellDelivery(windowTicks: 3600);
        var growth = new RawCapacityGrowth();
        var ready = new[] { Cell(IronRow, 0, "ready") with { Tick = 100 }, Cell(CoalRow, 0, "ready") with { Tick = 100 } };
        var products = new HashSet<string>(StringComparer.Ordinal) { "iron-plate", "coal", "iron-gear-wheel" };
        var first = Round(1000, ["coal", "iron-plate", "iron-gear-wheel"], iron: 5);
        delivery.Observe(first, ready);
        bool Left(string item, bool raw, LogisticsResult round) => FactoryResearchController.LeftToCells(item, raw, round, products, delivery, growth);
        // A delivering cell and a newly ready one cover their products; assembler products always wait for their cells.
        Assert.True(Left("iron-plate", true, first));
        Assert.True(Left("coal", true, first));
        Assert.True(Left("iron-gear-wheel", false, first));
        // Two minutes later the coal miner has delivered nothing: the actor procures coal again.
        var later = Round(8000, ["coal", "iron-plate"], iron: 5);
        delivery.Observe(later, ready);
        Assert.False(Left("coal", true, later));
        Assert.True(Left("iron-plate", true, later));
        // Boiler fuel is survival, and a failed growth hands its item back to ordinary procurement.
        var starved = Round(8100, ["coal"], coal: 3) with { PowerStarved = true };
        delivery.Observe(starved, ready);
        Assert.False(Left("coal", true, starved));
        Assert.True(Left("coal", true, starved with { PowerStarved = false }));
        growth.Failed("iron-plate");
        Assert.False(Left("iron-plate", true, later));
        Assert.False(Left("copper-plate", true, later)); // No cell ever delivered it.
    }

    private static LogisticsResult Round(long tick, string[] shortfall, long coal = 0, long iron = 0)
    {
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        if (coal > 0) collected["coal"] = coal;
        if (iron > 0) collected["iron-plate"] = iron;
        return new(collected, new Dictionary<string, long>(), shortfall.ToDictionary(item => item, _ => 10L, StringComparer.Ordinal), 1, tick);
    }

    private static FactoryCell Cell(ResourceRow row, int index, string status) => new($"cell-{row.Id}-{index}", 0, new(row.Id, index, true),
        row.Kind, row.Equipment.Drill, row.Product, new Dictionary<string, string>(), status, 1);
}
