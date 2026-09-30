using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StockCollectionBatchTests
{
    [Fact]
    public void TheNearestStockedOutputIsVisitedFirst()
    {
        // Campaign 2026-09-30 (seed 20261002): the first listed furnace was 60 tiles away while plates waited nearby.
        var state = new ProductionState(new("world", "session", "actor", 1, 1), 100, "ai", new Dictionary<string, long>(),
            [Furnace("far", new(-60, 0), 30), Furnace("near", new(4, 0), 2)], Position: new(0, 0));
        Assert.Equal("near", state.AvailableOutput("iron-plate")?.Id);
    }

    [Theory]
    [InlineData(80, 1, 100, 80)]
    [InlineData(250, 1, 100, 100)]
    [InlineData(250, 140, 100, 140)]
    [InlineData(3, 10, 100, 3)]
    public void OneVisitTakesUpToAStackSoTheNextIngredientNeedsNoTrip(long available, long missing, int stack, long expected) =>
        Assert.Equal(expected, ProductionState.CollectionCount(available, missing, stack));

    private static ProductionEntity Furnace(string id, MapPosition position, long plates) => new(id, "stone-furnace", position, "iron-plate",
        Protocol.ToElement(new { output = new { items = new Dictionary<string, long> { ["iron-plate"] = plates } } }));
}
