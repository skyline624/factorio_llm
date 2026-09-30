using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryRegistryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factory-registry-" + Guid.NewGuid().ToString("N"));
    public FactoryRegistryTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task CellEntitiesAreReservedFromActorDrivenProduction()
    {
        var registry = new FactoryRegistry(directory);
        var state = new FactoryState(1, "world", [], [
            new("ready", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["machine"] = "19", ["output-inserter"] = "22" }, "ready", 10),
            new("partial", 1, new(0, 0, false), "assembler", "assembling-machine-1", "automation-science-pack",
                new Dictionary<string, string> { ["pole"] = "30" }, "building", 11)
        ]);
        await registry.SaveAsync(state, default);
        using (ProductionReservations.Enter(await registry.CellEntityIdsAsync("world", default)))
        {
            Assert.Contains("19", ProductionReservations.Current);
            Assert.Contains("22", ProductionReservations.Current);
            Assert.Contains("30", ProductionReservations.Current);
        }
        Assert.Empty(ProductionReservations.Current);
    }

    [Fact]
    public async Task MissingRegistryReservesNothing()
    {
        using (ProductionReservations.Enter(await new FactoryRegistry(directory).CellEntityIdsAsync("world", default)))
            Assert.Empty(ProductionReservations.Current);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
