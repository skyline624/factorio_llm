using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ChemicalRelocationGuardTests
{
    [Theory]
    [InlineData(false, 0, 0, "water", false, true)]
    [InlineData(true, 0, 0, "water", false, false)]
    [InlineData(false, 1, 0, "water", false, false)]
    [InlineData(false, 0, 1, "water", false, false)]
    [InlineData(false, 0, 0, "acid", false, false)]
    [InlineData(false, 0, 0, "water", true, false)]
    public void OnlyIdleUnusedMachinesWithEmptyInventoriesCanDiscardMeasuredInputBuffers(
        bool engaged, int completed, int items, string fluid, bool shared, bool allowed)
    {
        var recipe = new NativeRecipe("sulfur", true, "chemistry", 1,
            [new("water", "fluid", 30)], [new("sulfur", "item", 2)], false);
        var snapshot = new FactorySnapshot("s", new("w", "s", "a", 1, 1), 1, 100, Protocol.ToElement(new { }),
        [
            new("m", "entity", "m", "chemical-plant", Protocol.ToElement(new { inventories = new[] { "input" } })),
            new("input", "inventory", "m", "input", Protocol.ToElement(new { items = new Dictionary<string, int> { ["sulfur"] = items } })),
            new("work", "work", "m", "work", Protocol.ToElement(new { recipe = "sulfur", inProcess = engaged, productsFinished = completed })),
            new("fluid", "fluid", "m", "buffer", Protocol.ToElement(new { aggregateSafe = true,
                contents = new Dictionary<string, double> { [fluid] = 60 }, sourceBoxes = shared
                    ? new[] { new { entityId = "m", index = 1 }, new { entityId = "other", index = 1 } }
                    : new[] { new { entityId = "m", index = 1 } } }))
        ]);
        if (allowed) Assert.Equal(60, ChemicalRelocationGuard.Inspect(snapshot, "m", recipe)["water"]);
        else Assert.Throws<InvalidOperationException>(() => ChemicalRelocationGuard.Inspect(snapshot, "m", recipe));
    }
}
