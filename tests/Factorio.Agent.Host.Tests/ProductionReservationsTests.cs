using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionReservationsTests
{
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
