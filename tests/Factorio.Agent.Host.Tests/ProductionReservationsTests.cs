using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionReservationsTests
{
    [Fact]
    public async Task ReadyMinerReuseSurvivesNestedProductionButAnExplicitReservationRevokesIt()
    {
        var cell = new FactoryCell("coal", 0, new(0, 0, true), "miner", "burner-mining-drill", "coal",
            new Dictionary<string, string> { ["drill"] = "drill", ["output-chest"] = "chest" }, "ready", 1);
        using (ProductionReservations.EnterFactory(new(1, "world", [], [cell])))
        {
            Assert.Contains("drill", ProductionReservations.Current);
            Assert.Contains("chest", ProductionReservations.Current);
            Assert.Equal("coal", Assert.Single(ProductionReservations.Extractors).Item);
            using (ProductionReservations.Enter(null))
            {
                await Task.Yield();
                Assert.Single(ProductionReservations.Extractors);
            }
            using (ProductionReservations.Enter(new HashSet<string> { "drill" }))
                Assert.Empty(ProductionReservations.Extractors);
            Assert.Single(ProductionReservations.Extractors);
            using (ProductionReservations.EnterFactory(new(1, "world", [], [cell with { Status = "building" }])))
                Assert.Empty(ProductionReservations.Extractors);
            Assert.Single(ProductionReservations.Extractors);
        }
        Assert.Empty(ProductionReservations.Extractors);
        Assert.Empty(ProductionReservations.Current);
    }

    [Fact]
    public async Task NestedProductionKeepsParentStocksReservedAndRestoresThePreviousScope()
    {
        using (ProductionReservations.Enter(new HashSet<string> { "producer" }))
        {
            await Task.Yield();
            Assert.Contains("producer", ProductionReservations.Current);
            using (ProductionReservations.Enter(new HashSet<string> { "buffer" }))
            {
                await Task.Yield();
                Assert.Contains("producer", ProductionReservations.Current);
                Assert.Contains("buffer", ProductionReservations.Current);
            }
            Assert.DoesNotContain("buffer", ProductionReservations.Current);
            Assert.Contains("producer", ProductionReservations.Current);
        }
        Assert.Empty(ProductionReservations.Current);
    }
}
