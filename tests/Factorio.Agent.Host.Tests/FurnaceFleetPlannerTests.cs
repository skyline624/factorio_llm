using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceFleetPlannerTests
{
    private static NativeRecipe Steel => new("steel", true, "smelting", 16,
        [new("iron", "item", 5)], [new("steel", "item", 1)], true);

    [Theory]
    [InlineData(20, 1, 1)]
    [InlineData(50, 1, 2)]
    [InlineData(100, 1, 3)]
    [InlineData(1000, 1, 8)]
    [InlineData(50, 2, 1)]
    public void CapacityUsesNativeWorkAndSpeedWithBoundedExpansion(int batches, double speed, int count) =>
        Assert.Equal(count, FurnaceFleetPlanner.RequiredMachines(Steel, batches, speed));

    [Fact]
    public void ColdFurnacesShareTheFirstLoadInsteadOfWaitingForOneToFinish()
    {
        var plan = FurnaceFleetPlanner.Plan(Steel, 50, 0, [new("a", 0, 0, false, 100, 1), new("b", 0, 0, false, 100, 1)]);
        Assert.Equal(2, plan.Loads.Count);
        Assert.All(plan.Loads, load => Assert.Equal(100, load.InputToInsert));
        Assert.Equal(40, plan.Loads.Sum(load => load.TotalCycles));
    }

    [Fact]
    public void CarriedReadyQueuedAndEngagedProductsAreNotOrderedTwice()
    {
        var plan = FurnaceFleetPlanner.Plan(Steel, 30, 10, [new("a", 40, 2, true, 60, 1), new("b", 40, 1, false, 60, 1)]);
        Assert.All(plan.Loads, load => Assert.Equal(0, load.InputToInsert));
        Assert.Equal(3, plan.ReadyOutput);
        Assert.Equal(17, plan.CommittedOutput);
    }

    [Fact]
    public void PartialIngredientsAndNativeInsertionCapacityBoundAdditionalSupply()
    {
        var plan = FurnaceFleetPlanner.Plan(Steel, 10, 0, [new("a", 3, 0, false, 7, 1), new("b", 0, 0, false, 8, 1)]);
        Assert.Equal(7, plan.Loads.Single(l => l.EntityId == "a").InputToInsert);
        Assert.Equal(5, plan.Loads.Single(l => l.EntityId == "b").InputToInsert);
        Assert.Equal(3, plan.Loads.Sum(l => l.TotalCycles));
    }

    [Fact]
    public void FasterFurnaceReceivesMoreWorkWhileInputProcurementStaysExecutable()
    {
        var recipe = Steel with { Ingredients = [new("iron", "item", 1)] };
        var plan = FurnaceFleetPlanner.Plan(recipe, 900, 0, [new("slow", 0, 0, false, 1000, 1), new("fast", 0, 0, false, 1000, 2)]);
        Assert.InRange(plan.Loads.Single(l => l.EntityId == "fast").InputToInsert, 599, 601);
        Assert.Equal(900, plan.Loads.Sum(l => l.InputToInsert));
        var large = FurnaceFleetPlanner.Plan(Steel, 1000, 0, [new("a", 0, 0, false, 1000, 1), new("b", 0, 0, false, 1000, 1)]);
        Assert.Equal(1000, large.Loads.Sum(l => l.InputToInsert));
    }

    [Fact]
    public void EngagedRawSmeltingKeepsItsIngredientsInTheFleetBalance()
    {
        var recipe = new NativeRecipe("stone-brick", true, "smelting", 3.2,
            [new("stone", "item", 2)], [new("stone-brick", "item", 1)], true);
        var plan = FurnaceFleetPlanner.Plan(recipe, 200, 0,
            [new("existing", 8, 0, true, 192, 1), new("new", 0, 0, false, 200, 1)]);
        Assert.Equal(5, plan.CommittedOutput);
        Assert.Equal(390, plan.Loads.Sum(load => load.InputToInsert));
        Assert.Equal(200, plan.Loads.Sum(load => load.TotalCycles));
    }

    [Fact]
    public void DuplicateMachinesAndNegativeStocksCannotCreateAProductionPlan()
    {
        var a = new FurnaceFleetMachine("same", 0, 0, false, 100, 1);
        Assert.Throws<InvalidDataException>(() => FurnaceFleetPlanner.Plan(Steel, 50, 0, [a, a]));
        Assert.Throws<InvalidDataException>(() => FurnaceFleetPlanner.Plan(Steel, 50, 0, [a with { Input = -1 }]));
    }

    [Fact]
    public void AtomicReadingUsesCurrentNativeSpeedAndRetainsAnEngagedCycle()
    {
        var work = new FactoryRecord("work:a", "work", "a", "machine-craft", Protocol.ToElement(new
        { recipe = "steel", inputInventoryId = "input:a", outputInventoryId = "output:a", inProcess = true, craftingSpeed = 2.25 }));
        var input = new FactoryRecord("input:a", "inventory", "a", "input", Protocol.ToElement(new
        { items = new { iron = 13 }, capacityHints = new { iron = new { insertable = 87 } } }));
        var output = new FactoryRecord("output:a", "inventory", "a", "output", Protocol.ToElement(new { items = new { steel = 4 } }));
        var snapshot = new FactorySnapshot("sample", new("w", "s", "actor", 1, 1), 100, 200, Protocol.ToElement(new { }), [work, input, output]);
        var reading = FurnaceFleetPlanner.Read(snapshot, "a", Steel);
        Assert.Equal(2.25, reading.CraftingSpeed);
        Assert.True(reading.InProcess);
        var plan = FurnaceFleetPlanner.Plan(Steel, 7, 0, [reading]);
        Assert.Equal(4, plan.ReadyOutput);
        Assert.Equal(3, plan.CommittedOutput);
        Assert.Equal(0, Assert.Single(plan.Loads).InputToInsert);
    }
}
