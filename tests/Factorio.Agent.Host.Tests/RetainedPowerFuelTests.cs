using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RetainedPowerFuelTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(200, 0, 0, true)]
    [InlineData(49, 5, 1000, true)]
    [InlineData(50, 5, 1000, false)]
    [InlineData(200, 0, 1000, false)]
    public void FeederReserveAndActualBurningFuelDetermineRecovery(long chest, long boiler, double burning, bool needed)
    {
        Assert.Equal(needed, PowerExpansionController.NeedsPriming(Snapshot(chest, boiler, burning), Cell, 50));
    }

    [Fact]
    public void ForeignChestMaterialsAndChangedNativeTargetsRefuseRecovery()
    {
        var mixed = Snapshot(0, 0, 0);
        mixed = mixed with { Records = mixed.Records.Select(r => r.Id == "chest-items"
            ? r with { Data = Protocol.ToElement(new { items = new Dictionary<string, long> { ["iron-plate"] = 1 } }) } : r).ToArray() };
        Assert.Throws<InvalidDataException>(() => PowerExpansionController.NeedsPriming(mixed, Cell, 50));
        var changed = Snapshot(0, 0, 0) with { Records = Snapshot(0, 0, 0).Records.Select(r => r.EntityId == "arm"
            ? r with { Data = Protocol.ToElement(new { transport = new { pickupTargetId = "foreign-chest", dropTargetId = "boiler" } }) } : r).ToArray() };
        Assert.Throws<InvalidDataException>(() => PowerExpansionController.ValidateRegisteredFeeder(changed, Cell));
        PowerExpansionController.ValidateRegisteredFeeder(Snapshot(0, 0, 0), Cell);
    }

    private static readonly FactoryCell Cell = new("power", 0, new(0, 0, true), "power", "boiler", null,
        new Dictionary<string, string> { ["boiler"] = "boiler", ["input-chest"] = "chest", ["input-inserter"] = "arm" }, "ready", 1);

    private static FactorySnapshot Snapshot(long chest, long boiler, double burning) => new("native", new("world", "session", "actor", 1, 1),
        1, 5, Protocol.ToElement(new { atomic = true }),
        [new("boiler", "entity", "boiler", "boiler", Protocol.ToElement(new { burnerRemainingJoules = burning })),
         new("boiler-items", "inventory", "boiler", "fuel", Protocol.ToElement(new { items = new Dictionary<string, long> { ["coal"] = boiler } })),
         new("chest-items", "inventory", "chest", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { ["coal"] = chest } })),
         new("arm", "entity", "arm", "inserter", Protocol.ToElement(new { transport = new { pickupTargetId = "chest", dropTargetId = "boiler" } }))]);
}
