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
    public void RecurringRawShortfallGrowsCapacityWithinItsBoundsAndStopsAfterAFailure()
    {
        var growth = new RawCapacityGrowth(persistentRounds: 2, maximumCells: 2);
        growth.Observe(["iron-plate"]);
        Assert.False(growth.Due("iron-plate"));
        growth.Observe([]);
        growth.Observe(["iron-plate"]);
        Assert.True(growth.Due("iron-plate"));
        growth.Grew("iron-plate");
        Assert.False(growth.Due("iron-plate"));
        growth.Observe(["iron-plate"]);
        growth.Observe(["iron-plate"]);
        Assert.True(growth.Due("iron-plate"));
        growth.Grew("iron-plate");
        growth.Observe(["iron-plate"]);
        growth.Observe(["iron-plate"]);
        Assert.False(growth.Due("iron-plate")); // Per-run cell budget reached.
        growth.Observe(["coal"]);
        growth.Observe(["coal"]);
        Assert.True(growth.Due("coal"));
        growth.Failed("coal");
        growth.Observe(["coal"]);
        Assert.False(growth.Due("coal"));
        Assert.Equal(RawCapacityGrowth.DefaultPerMinute, RawCapacityGrowth.TargetRate(0, 0));
        Assert.Equal(12, RawCapacityGrowth.TargetRate(12, 0));
        Assert.Equal(40, RawCapacityGrowth.TargetRate(12, 20));
    }

    private static FactoryCell Cell(ResourceRow row, int index, string status) => new($"cell-{row.Id}-{index}", 0, new(row.Id, index, true),
        row.Kind, row.Equipment.Drill, row.Product, new Dictionary<string, string>(), status, 1);
}
