using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConstructionSmeltingContextTests
{
    [Fact]
    public async Task NestedProcurementKeepsItsContextAndRestoresItAfterFailure()
    {
        Assert.False(CarriedStock.IsProcuringConstructionStock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CarriedStock.EnsureAsync(new Executor(async () =>
        {
            Assert.True(CarriedStock.IsProcuringConstructionStock);
            await CarriedStock.EnsureAsync(new Executor(() =>
            {
                Assert.True(CarriedStock.IsProcuringConstructionStock);
                return Task.CompletedTask;
            }), new Dictionary<string, long>(), "stone-furnace", 1, CancellationToken.None);
            Assert.True(CarriedStock.IsProcuringConstructionStock);
            throw new InvalidOperationException("synthetic procurement failure");
        }), new Dictionary<string, long>(), "steel-plate", 15, CancellationToken.None));
        Assert.False(CarriedStock.IsProcuringConstructionStock);
    }

    [Fact]
    public async Task ACompleteCarriedKitDoesNotEnterAProcurementContext()
    {
        int calls = 0;
        await CarriedStock.EnsureAsync(new Executor(() => { calls++; return Task.CompletedTask; }),
            new Dictionary<string, long> { ["steel-plate"] = 15 }, "steel-plate", 15, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.False(CarriedStock.IsProcuringConstructionStock);
    }

    private sealed class Executor(Func<Task> action) : IStockGoalExecutor
    {
        public async Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default)
        {
            await Task.Yield();
            await action();
            return new("synthetic", item, targetStock, 0, targetStock, 0, 1);
        }
    }
}
