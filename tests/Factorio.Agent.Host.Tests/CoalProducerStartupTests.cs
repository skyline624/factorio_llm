using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CoalProducerStartupTests
{
    private static readonly FactoryCell Cell = new("coal-cell", 0, new(1, 0, true), "miner", "burner-mining-drill", "coal",
        new Dictionary<string, string> { ["drill"] = "drill" }, "ready", 1);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(4_000_000, 0, false)]
    public void LoadedOrAlreadyBurningFuelDoesNotTriggerAnotherStartup(double burning, long loaded, bool cold)
    {
        Assert.Equal(cold, CoalProducerStartup.Cold(Snapshot(burning, loaded), Cell));
    }

    [Fact]
    public void AnElectricProducerDoesNotRequestBurnerFuel()
    {
        var snapshot = Snapshot(0, 0);
        var records = snapshot.Records.Where(r => r.Kind == "entity").Select(r => r with { Data = JsonSerializer.SerializeToElement(new { }) }).ToArray();
        Assert.False(CoalProducerStartup.Cold(snapshot with { Records = records }, Cell));
    }

    [Fact]
    public void ExistingDifferentFuelIsPreservedRatherThanMixedWithCoal()
    {
        Assert.False(CoalProducerStartup.Cold(Snapshot(0, 1, "wood"), Cell));
    }

    [Fact]
    public void ADisappearedDrillCannotBeTreatedAsAColdProducer()
    {
        var snapshot = Snapshot(0, 0);
        Assert.Throws<InvalidOperationException>(() => CoalProducerStartup.Cold(snapshot with { Records = [] }, Cell));
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
        Assert.Equal(expected, CoalProducerStartup.StarterCount(carried, capacity, stack));
    }

    private static FactorySnapshot Snapshot(double burning, long loaded, string fuel = "coal") => new("fixture",
        ConstructionSupplyPlannerTests.Catalog().Scope, 1, 100, JsonSerializer.SerializeToElement(new { }),
        [new("drill", "entity", "drill", "burner-mining-drill", JsonSerializer.SerializeToElement(new { burnerRemainingJoules = burning, fuelInventoryId = "fuel" })),
         new("fuel", "inventory", "drill", "fuel", JsonSerializer.SerializeToElement(new { items = new Dictionary<string, long> { [fuel] = loaded } }))]);
}
