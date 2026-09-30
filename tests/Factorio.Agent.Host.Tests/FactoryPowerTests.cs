using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryPowerTests
{
    private static FactoryRecord Entity(string id, string type, long? network) => new(id, "entity", id, type,
        JsonSerializer.SerializeToElement(new { role = "factory", type, electricNetworkId = network }));

    private static FactorySnapshot Snapshot(params FactoryRecord[] records) => new("s", new("w", "s", "a", 1, 1), 10, 20,
        JsonSerializer.SerializeToElement(new { atomic = true }), records);

    [Fact]
    public void APoleIslandWithoutAGeneratorIsNotFed()
    {
        // Two cell poles and their machine share a network that no generator reaches.
        var snapshot = Snapshot(Entity("18", "electric-pole", 7), Entity("24", "electric-pole", 7), Entity("19", "assembling-machine", 7),
            Entity("14", "generator", 3), Entity("15", "electric-pole", 3));
        Assert.False(FactoryPower.IsFed(snapshot, "18"));
        Assert.True(FactoryPower.IsFed(snapshot, "15"));
    }

    [Fact]
    public void AnyNativePowerSourceFeedsItsNetwork()
    {
        var snapshot = Snapshot(Entity("1", "electric-energy-interface", 4), Entity("2", "electric-pole", 4));
        Assert.True(FactoryPower.IsFed(snapshot, "2"));
    }

    [Fact]
    public void OlderModsWithoutNetworkIdsGiveNoProof()
    {
        var snapshot = Snapshot(new FactoryRecord("2", "entity", "2", "pole", JsonSerializer.SerializeToElement(new { role = "factory", type = "electric-pole" })));
        Assert.Null(FactoryPower.IsFed(snapshot, "2"));
        Assert.False(FactoryPower.IsFed(snapshot, "missing"));
    }
}
