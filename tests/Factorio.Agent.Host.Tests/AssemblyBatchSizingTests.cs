using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AssemblyBatchSizingTests
{
    private static readonly NativeRecipe Circuit = new("electronic-circuit", true, "crafting", .5,
        [new("iron-plate", "item", 1), new("copper-cable", "item", 3)], [new("electronic-circuit", "item", 1)], false);
    private static readonly IReadOnlyDictionary<string, NativeItem> Items = new Dictionary<string, NativeItem>
    {
        ["iron-plate"] = new(0, 100), ["copper-cable"] = new(0, 200)
    };

    [Theory]
    [InlineData(200, 66)]
    [InlineData(40, 40)]
    [InlineData(7, 7)]
    public void CircuitDeliveryUsesTheNativeCableStackAndRemainingGoal(int requested, int expected)
        => Assert.Equal(expected, AssemblyPlanner.DeliveryBatchLimit(Circuit, Items, requested));

    [Fact]
    public void RepeatedIngredientEntriesShareOneStack()
    {
        var recipe = Circuit with { Ingredients = [new("copper-cable", "item", 2), new("copper-cable", "item", 3)] };
        Assert.Equal(40, AssemblyPlanner.DeliveryBatchLimit(recipe, Items, 200));
    }

    [Fact]
    public void SmallNativeStacksRemainBinding()
    {
        var items = new Dictionary<string, NativeItem>(Items) { ["copper-cable"] = new(0, 12) };
        Assert.Equal(4, AssemblyPlanner.DeliveryBatchLimit(Circuit, items, 200));
    }

    [Fact]
    public void MissingStackEvidenceAndOversizedCyclesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => AssemblyPlanner.DeliveryBatchLimit(Circuit, new Dictionary<string, NativeItem>(), 200));
        var recipe = Circuit with { Ingredients = [new("copper-cable", "item", 201)] };
        Assert.Throws<InvalidOperationException>(() => AssemblyPlanner.DeliveryBatchLimit(recipe, Items, 200));
    }

    [Fact]
    public void FluidOnlyRecipesKeepTheirBoundWithoutInventingAnItemStack()
    {
        var recipe = Circuit with { Ingredients = [new("water", "fluid", 100)] };
        Assert.Equal(16, AssemblyPlanner.DeliveryBatchLimit(recipe, Items, 200));
        Assert.Equal(7, AssemblyPlanner.DeliveryBatchLimit(recipe, Items, 7));
    }

    [Fact]
    public void DeliveriesRemainBoundedEvenWithLargeNativeStacks()
    {
        var items = new Dictionary<string, NativeItem>(Items)
            { ["iron-plate"] = new(0, 100000), ["copper-cable"] = new(0, 100000) };
        Assert.Equal(1000, AssemblyPlanner.DeliveryBatchLimit(Circuit, items, 20000));
    }
}
