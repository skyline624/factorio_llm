using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class MachineInputRequirementsTests
{
    [Fact]
    public void SulfurKeepsItsTwoFluidStocksSeparateAndSubtractsTheEngagedCycle()
    {
        var recipe = new NativeRecipe("sulfur", true, "chemistry", 1,
            [new("water", "fluid", 30), new("gas", "fluid", 30)], [new("sulfur", "item", 2)], true);
        var needed = MachineInputRequirements.From(Snapshot("sulfur", true, [], 20, 5), "machine", recipe, 3);
        Assert.True(needed.InProcess);
        Assert.Empty(needed.Items);
        Assert.Equal(40, needed.Fluids["water"]);
        Assert.Equal(55, needed.Fluids["gas"]);
    }

    [Fact]
    public void AcidSubtractsEngagedAndStockedIngredientsWithoutConvertingWaterToInventoryItems()
    {
        var recipe = new NativeRecipe("acid", true, "chemistry", 1,
            [new("iron-plate", "item", 1), new("sulfur", "item", 5), new("water", "fluid", 100)], [new("acid", "fluid", 50)], true);
        var stock = new Dictionary<string, int> { ["iron-plate"] = 1, ["sulfur"] = 2 };
        var needed = MachineInputRequirements.From(Snapshot("acid", true, stock, 75, 0), "machine", recipe, 3);
        Assert.Equal(1, needed.Items["iron-plate"]);
        Assert.Equal(8, needed.Items["sulfur"]);
        Assert.Equal(125, needed.Fluids["water"]);
        Assert.DoesNotContain("water", needed.Items.Keys);
    }

    private static FactorySnapshot Snapshot(string recipe, bool inProcess, Dictionary<string, int> items, double water, double gas) =>
        new("s", new("w", "s", "a", 1, 1), 1, 100, Protocol.ToElement(new { }),
        [new("input", "inventory", "machine", "input", Protocol.ToElement(new { items })),
         new("water", "fluid", "machine", "fluid", Protocol.ToElement(new { aggregateSafe = true,
             contents = new Dictionary<string, double> { ["water"] = water }, sourceBoxes = new[] { new { entityId = "machine", index = 1 } } })),
         new("gas", "fluid", "machine", "fluid", Protocol.ToElement(new { aggregateSafe = true,
             contents = new Dictionary<string, double> { ["gas"] = gas }, sourceBoxes = new[] { new { entityId = "machine", index = 2 } } })),
         new("work", "work", "machine", "machine-craft", Protocol.ToElement(new { recipe, inProcess, inputInventoryId = "input" }))]);
}
