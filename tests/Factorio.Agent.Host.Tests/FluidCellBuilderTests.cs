using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidCellBuilderTests
{
    [Fact]
    public void AnExtractorRateComesFromTheDepositUnderItsDrill()
    {
        var deposit = OilMaps.Crude("crude", new(8.5, .5), 150000);
        var jack = new SpatialEntity("jack", "pumpjack", deposit.Position, new WorldBox(new(-1.2, -1.2), new(1.2, 1.2)).Translate(deposit.Position), 0, "agent");
        var cell = Extractor("jack");
        // resources.lua: 150000 of a 300000 normal amount is a 50 % yield, 5 crude oil per second.
        Assert.Equal(300, FluidCellBuilder.Rate(OilMaps.Map([deposit, jack]), OilCatalogs.Oil(), cell)!.Value, 6);
        Assert.Null(FluidCellBuilder.Rate(OilMaps.Map([deposit]), OilCatalogs.Oil(), cell));
        Assert.Null(FluidCellBuilder.Rate(OilMaps.Map([jack]), OilCatalogs.Oil(), cell));
    }

    [Fact]
    public void TheNearestObservedTerrainFluidTileAnchorsANewPump()
    {
        var map = FactoryMaps.Grass(20, tile: (x, _) => x >= 10 ? "water" : "grass") with
        {
            TileFluids = new Dictionary<string, string> { ["water"] = "water" }
        };
        Assert.Equal(new MapPosition(10.5, -.5), FluidCellBuilder.Shore(map, "water", new(0, 0)));
        Assert.Null(FluidCellBuilder.Shore(map, "crude-oil", new(0, 0)));
    }

    [Fact]
    public void AnExtractorPlanNamesItsMachineDrillLikeMinerCells()
    {
        var map = OilMaps.Map([]);
        var layout = new FluidCellPlanner().Layouts(map, new("pumpjack", "inserter", "iron-chest", "small-electric-pole"),
            new(new(.5, .5), 0, 0), false, false).First();
        var plan = FluidCellBuilder.Roles(layout, "drill");
        // Maintenance configures a rebuilt "machine" with the cell recipe; an extractor's recipe field holds its product instead.
        Assert.Equal(["drill", "pole"], plan.Keys.Order());
        Assert.Equal("pumpjack", plan["drill"].Item);
    }

    private static FactoryCell Extractor(string drill) => new("cell", 0, new(0, 0, true), FluidCellBuilder.ExtractorKind, "pumpjack", "crude-oil",
        new Dictionary<string, string> { ["drill"] = drill }, "ready", 1);
}
