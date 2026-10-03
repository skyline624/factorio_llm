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

    private static FactorySnapshot Snapshot(double burning, long loaded, string fuel = "coal") => new("fixture",
        ConstructionSupplyPlannerTests.Catalog().Scope, 1, 100, JsonSerializer.SerializeToElement(new { }),
        [new("drill", "entity", "drill", "burner-mining-drill", JsonSerializer.SerializeToElement(new { burnerRemainingJoules = burning, fuelInventoryId = "fuel" })),
         new("fuel", "inventory", "drill", "fuel", JsonSerializer.SerializeToElement(new { items = new Dictionary<string, long> { [fuel] = loaded } }))]);
}
