using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RecipeFuelReserveTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
    private static readonly FactoryCell Plastic = new("plastic", 0, new(0, 0, true), FluidCellBuilder.MachineKind, "chemical-plant", "plastic-bar",
        new Dictionary<string, string> { ["machine"] = "plant", ["input-chest"] = "coal-chest", ["output-chest"] = "plastic-chest" }, "ready", 1);

    [Fact]
    public void ScarceCoalLiftsAStarvedBoilerToItsThresholdBeforeAPlasticCellTakesAny()
    {
        // The boiler holds 5 coal, below a quarter of a 50-coal stack: 7 coal stay out of the plastic chest.
        var snapshot = Snapshot([
            Entity("boiler", "boiler", fuel: "boiler-fuel"), Inventory("boiler-fuel", "boiler", new { coal = 5 }),
            Entity("plant", "assembling-machine"), Entity("coal-chest", "container"), Inventory("coal-chest-main", "coal-chest", new { })]);
        Assert.Equal(7, FactoryLogistics.FuelReserve(snapshot, [Plastic], 50));
        // A boiler at its threshold leaves every carried coal to recipes.
        var fed = Snapshot([Entity("boiler", "boiler", fuel: "boiler-fuel"), Inventory("boiler-fuel", "boiler", new { coal = 12 })]);
        Assert.Equal(0, FactoryLogistics.FuelReserve(fed, [Plastic], 50));
    }

    [Theory]
    [InlineData(2, 3, false, 7)]
    [InlineData(20, 3, false, 0)]
    [InlineData(2, 3, true, 0)]
    [InlineData(2, 0, true, 12)]
    public void FeederSuppliesReachAQuarterStackChestAndBoilerTogether(long chest, long boiler, bool mixed, long reserve)
    {
        // A mixed chest is never topped up; its boiler is only fuelled by hand once dry.
        var power = new FactoryCell("power", PowerExpansionController.PowerZone, new(0, 0, true), "power", "boiler", null,
            new Dictionary<string, string> { ["boiler"] = "boiler", ["input-chest"] = "feeder" }, "ready", 1);
        object contents = mixed ? new { coal = chest, wood = 1 } : new { coal = chest };
        var snapshot = Snapshot([
            Entity("boiler", "boiler", fuel: "boiler-fuel"), Inventory("boiler-fuel", "boiler", new { coal = boiler }),
            Entity("feeder", "container"), Inventory("feeder-main", "feeder", contents)]);
        Assert.Equal(reserve, FactoryLogistics.FuelReserve(snapshot, [power, Plastic], 50));
    }

    [Fact]
    public void CellFurnacesKeepTheFuelThatRestartsThem()
    {
        var smelter = new FactoryCell("smelter", 0, new(1, 0, true), "smelter", "electric-mining-drill", "iron-plate",
            new Dictionary<string, string> { ["furnace"] = "furnace", ["output-chest"] = "plates" }, "ready", 1);
        var snapshot = Snapshot([Entity("furnace", "furnace", fuel: "furnace-fuel"), Inventory("furnace-fuel", "furnace", new { coal = 2 })]);
        Assert.Equal(10, FactoryLogistics.FuelReserve(snapshot, [smelter, Plastic], 50));
    }

    private static FactorySnapshot Snapshot(FactoryRecord[] records) => new("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), records);

    private static FactoryRecord Entity(string id, string type, string? fuel = null) => new(id, "entity", id, type,
        fuel is null ? Protocol.ToElement(new { role = "factory", type }) : Protocol.ToElement(new { role = "factory", type, fuelInventoryId = fuel }));

    private static FactoryRecord Inventory(string id, string owner, object items) =>
        new(id, "inventory", owner, "main", Protocol.ToElement(new { role = "factory", items }));
}
