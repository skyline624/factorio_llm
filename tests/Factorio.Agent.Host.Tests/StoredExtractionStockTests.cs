using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StoredExtractionStockTests
{
    [Theory]
    [InlineData(0, 0, 1000000, 0)]
    [InlineData(0, 0, 0, 50)]
    [InlineData(4, 2, 100, 46)]
    public void ReadsRealStorageAndBurnerStateWithoutTreatingAnEmptyFuelSlotAsEmptyEnergy(long output, long fuel, double burning, long capacity)
    {
        var snapshot = new FactorySnapshot("snapshot", new("world", "session", "actor", 1, 1), 100, 200, Protocol.ToElement(new { }), [
            new("store", "inventory", "chest", "chest", Protocol.ToElement(new { items = new { coal = output }, capacityHints = new { coal = new { insertable = capacity } } })),
            new("drill", "entity", "drill", "drill", Protocol.ToElement(new { fuelInventoryId = "native-fuel", burnerRemainingJoules = burning })),
            new("native-fuel", "inventory", "drill", "fuel", Protocol.ToElement(new { items = new { coal = fuel } }))]);
        var stock = StoredExtractionStock.From(snapshot, "drill", "chest", "coal");
        Assert.Equal(output, stock.Output);
        Assert.Equal(fuel, stock.StoredFuel);
        Assert.Equal(capacity, stock.Insertable);
        Assert.Equal(burning, stock.BurningJoules);
    }
}
