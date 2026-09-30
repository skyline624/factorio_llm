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
        using (ProductionReservations.EnterFactory(await registry.LoadAsync("world", default)))
        {
            Assert.Contains("19", ProductionReservations.Current);
            Assert.Contains("22", ProductionReservations.Current);
            Assert.Contains("30", ProductionReservations.Current);
            Assert.Empty(ProductionReservations.Collectable);
        }
        Assert.Empty(ProductionReservations.Current);
    }

    [Fact]
    public async Task ReadyOutputChestsStayReservedButTheirStockIsCollectable()
    {
        // Campaign 2026-09-30 (seed 20261002): cell builds procured plates from the actor's single early drill because
        // every cell chest, finished stock included, was hidden from collection.
        var state = new FactoryState(1, "world", [], [
            new("iron", 0, new(1, 0, true), "smelter", "burner-mining-drill", "iron-plate",
                new Dictionary<string, string> { ["furnace"] = "5", ["output-chest"] = "6" }, "ready", 10),
            new("partial", 1, new(0, 0, false), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["output-chest"] = "7" }, "building", 11)
        ]);
        using (ProductionReservations.EnterFactory(state))
        {
            Assert.Equal(["5", "6", "7"], ProductionReservations.Current.Order(StringComparer.Ordinal));
            Assert.Equal(["6"], ProductionReservations.Collectable);
            var chest = new ProductionEntity("6", "iron-chest", new(0, 0), null,
                Protocol.ToElement(new { output = new { items = new Dictionary<string, long> { ["iron-plate"] = 40 } } }));
            var furnace = chest with { Id = "5", Name = "stone-furnace" };
            var production = new ProductionState(new("world", "session", "actor", 1, 1), 1, "ai", new Dictionary<string, long>(), [furnace, chest]);
            Assert.Equal("6", production.AvailableOutput("iron-plate")?.Id);
            Assert.Equal("6", production.AvailableOutput("iron-plate", new HashSet<string> { "5" })?.Id);
            Assert.Null(production.AvailableOutput("iron-plate", new HashSet<string> { "6" }));
        }
        Assert.Empty(ProductionReservations.Collectable);
    }

    [Fact]
    public async Task MissingRegistryReservesNothing()
    {
        using (ProductionReservations.EnterFactory(await new FactoryRegistry(directory).LoadAsync("world", default)))
            Assert.Empty(ProductionReservations.Current);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
