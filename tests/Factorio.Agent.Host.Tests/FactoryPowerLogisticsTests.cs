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
}
