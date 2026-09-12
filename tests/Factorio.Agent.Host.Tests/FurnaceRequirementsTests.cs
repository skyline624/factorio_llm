using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceRequirementsTests
{
    [Theory]
    [InlineData(6, 0, false, 0)]
    [InlineData(5, 0, true, 0)]
    [InlineData(4, 1, true, 0)]
    [InlineData(4, 1, false, 1)]
    public void Input_work_in_progress_and_output_are_accounted_without_double_demand(
        int input, int output, bool crafting, int expectedMissing)
    {
        var recipe = new NativeRecipe("iron-plate", true, "smelting", 3.2,
            [new("iron-ore", "item", 1)], [new("iron-plate", "item", 1)], false);
        var snapshot = new FactorySnapshot("photo", new("world", "session", "actor", 1, 1), 100, 200, Protocol.ToElement(new { }), [
            new("input", "inventory", "furnace", "input", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-ore"] = input } })),
            new("output", "inventory", "furnace", "output", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-plate"] = output } })),
            new("work", "work", "furnace", "machine-craft", Protocol.ToElement(new { recipe = "iron-plate", inProcess = crafting, inputInventoryId = "input", outputInventoryId = "output" }))]);
        FurnaceRequirements result = FurnaceRequirements.From(snapshot, "furnace", recipe, 6);
        Assert.Equal(expectedMissing, result.InputToInsert);
        Assert.Equal(output, result.ReadyOutput);
        Assert.Equal(crafting, result.InProcess);
    }
}
