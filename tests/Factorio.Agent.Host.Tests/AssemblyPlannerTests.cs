using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AssemblyPlannerTests
{
    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(3, 1, 0)]
    [InlineData(1, 2, 1)]
    public void PartialOutputTransitReducesTheRemainingProductionBatches(long transit, double yield, int expected)
    {
        Assert.Equal(expected, AssemblyRequirements.BatchesAfterTransit(65, 62, transit, yield, 16));
    }

    private static readonly NativeRecipe Circuit = new("electronic-circuit", true, "crafting", .5,
        [new("iron-plate", "item", 1), new("copper-cable", "item", 3)], [new("electronic-circuit", "item", 1)], false);

    [Theory]
    [InlineData(66, 198, false, true)]
    [InlineData(1, 3, false, true)]
    [InlineData(0, 0, true, true)]
    [InlineData(1, 2, false, false)]
    [InlineData(0, 0, false, false)]
    public void ProcurementWaitsUntilNoCompleteNativeCycleRemains(int iron, int cable, bool engaged, bool supplied)
    {
        var snapshot = new FactorySnapshot("s", new("w", "s", "a", 1, 1), 10, 100, Protocol.ToElement(new { }),
        [new("input", "inventory", "machine", "input", Protocol.ToElement(new
            { items = new Dictionary<string, int> { ["iron-plate"] = iron, ["copper-cable"] = cable } })),
         new("output", "inventory", "machine", "output", Protocol.ToElement(new { items = new Dictionary<string, int>() })),
         new("work", "work", "machine", "machine-craft", Protocol.ToElement(new
            { recipe = Circuit.Name, inProcess = engaged, inputInventoryId = "input", outputInventoryId = "output" }))]);
        Assert.Equal(supplied, AssemblyRequirements.HasSuppliedCycle(snapshot, "machine", Circuit));
    }

    [Fact]
    public void ChemicalRecipeRequiresObservedFluidCapability()
    {
        var recipe = new NativeRecipe("plastic", true, "chemistry", 1,
            [new("coal", "item", 1), new("gas", "fluid", 20)], [new("plastic", "item", 2)], true);
        var machines = new Dictionary<string, NativeAssembler>
        {
            ["dry"] = new("dry", new Dictionary<string, bool> { ["chemistry"] = true }, 1, 1000, 255),
            ["chemical-plant"] = new("chemical-plant", new Dictionary<string, bool> { ["chemistry"] = true }, 1, 1000, 255,
                FluidInputCount: 2, FluidOutputCount: 2)
        };
        var plan = new AssemblyPlanner().Choose("plastic", [recipe], machines, []);
        Assert.NotNull(plan);
        Assert.Equal("chemical-plant", plan.MachineItem);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10, 50)]
    [InlineData(100, 0)]
    public void ChemicalAccountingKeepsFluidsOutOfInventoryDeliveries(double stockedFluid, double expectedSupply)
    {
        var recipe = new NativeRecipe("plastic", true, "chemistry", 1,
            [new("coal", "item", 1), new("gas", "fluid", 20)], [new("plastic", "item", 2)], true);
        var snapshot = new FactorySnapshot("s", new("w", "s", "a", 1, 1), 10, 100, Protocol.ToElement(new { }),
        [new("input", "inventory", "machine", "input", Protocol.ToElement(new { items = new Dictionary<string, int> { ["coal"] = 1 } })),
         new("output", "inventory", "machine", "output", Protocol.ToElement(new { items = new Dictionary<string, int> { ["plastic"] = 2 } })),
         new("gas-buffer", "fluid", "machine", "buffer", Protocol.ToElement(new { aggregateSafe = true,
             contents = new Dictionary<string, double> { ["gas"] = stockedFluid }, sourceBoxes = new[] { new { entityId = "machine", index = 1 } } })),
         new("work", "work", "machine", "machine-craft", Protocol.ToElement(new { recipe = "plastic", inProcess = true,
             inputInventoryId = "input", outputInventoryId = "output" }))]);
        var needed = AssemblyRequirements.From(snapshot, "machine", recipe, 5);
        Assert.Single(needed.InputsToInsert);
        Assert.Equal(2, needed.InputsToInsert["coal"]);
        Assert.DoesNotContain("gas", needed.InputsToInsert.Keys);
        Assert.Equal(expectedSupply, needed.FluidUnitsToSupply["gas"]);
    }

    [Fact]
    public void EngagedCraftAndReadyOutputAreSubtractedFromEveryIngredient()
    {
        var snapshot = new FactorySnapshot("s", new("w", "s", "a", 1, 1), 10, 100, Protocol.ToElement(new { }),
        [new("input", "inventory", "machine", "input", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-plate"] = 1, ["copper-cable"] = 2 } })),
         new("output", "inventory", "machine", "output", Protocol.ToElement(new { items = new Dictionary<string, int> { ["electronic-circuit"] = 2 } })),
         new("modules", "inventory", "machine", "modules", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-plate"] = 100 } })),
         new("work", "work", "machine", "machine-craft", Protocol.ToElement(new { recipe = Circuit.Name, inProcess = true, inputInventoryId = "input", outputInventoryId = "output" }))]);
        var needed = AssemblyRequirements.From(snapshot, "machine", Circuit, 5);
        Assert.Equal(1, needed.InputsToInsert["iron-plate"]);
        Assert.Equal(4, needed.InputsToInsert["copper-cable"]);
        Assert.Equal(2, needed.ReadyOutput);
        Assert.True(needed.InProcess);
    }

    [Fact]
    public void CapabilitySelectionHonorsIngredientLimitAndFixedRecipe()
    {
        var machines = new Dictionary<string, NativeAssembler>
        {
            ["too-small"] = new("small", new Dictionary<string, bool> { ["crafting"] = true }, 1, 1000, 1),
            ["fixed"] = new("fixed", new Dictionary<string, bool> { ["crafting"] = true }, 1, 1000, 2, "different"),
            ["assembler"] = new("assembler", new Dictionary<string, bool> { ["crafting"] = true }, .5, 1000, 2)
        };
        var plan = new AssemblyPlanner().Choose(Circuit.Products[0].Name, [Circuit], machines, []);
        Assert.Equal("assembler", plan!.MachineItem);
    }

    [Fact]
    public void ProductionReusesConfiguredAssemblerButLeavesIdleOneForHandCrafting()
    {
        var catalog = new ProductionCatalog(new("w", "s", "a", 1, 1), 10, [Circuit],
            new Dictionary<string, NativeItem>(), new Dictionary<string, NativeMaterial[]>(),
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool> { ["crafting"] = true },
            new Dictionary<string, NativeAssembler> { ["assembler"] = new("assembler", new Dictionary<string, bool> { ["crafting"] = true }, .5, 1000, 2) });
        var stock = new Dictionary<string, long> { ["iron-plate"] = 1, ["copper-cable"] = 3 };
        var map = SpatialPlannerTests.Map([]);
        catalog = catalog with { Scope = map.Scope };
        var planner = new ProductionPlanner();
        Assert.Equal("assemble", planner.Next("electronic-circuit", 1, stock, catalog, map,
            [new("1", "assembler", Circuit.Name)]).Kind);
        Assert.Equal("craft", planner.Next("electronic-circuit", 1, stock, catalog, map,
            [new("1", "assembler", null)]).Kind);
    }

    [Fact]
    public void ExistingRecipeWithUnrelatedOutputIsNotReused()
    {
        var machines = new Dictionary<string, NativeAssembler>
        {
            ["assembler"] = new("assembler", new Dictionary<string, bool> { ["crafting"] = true }, .5, 1000, 2)
        };
        var known = new KnownProductionMachine("old", "assembler", Circuit.Name, Output: new Dictionary<string, long> { ["wood"] = 1 });
        var plan = new AssemblyPlanner().Choose("electronic-circuit", [Circuit], machines, [known]);
        Assert.Null(plan!.ExistingId);
        var fluid = Circuit with { Ingredients = [new("water", "fluid", 1)] };
        Assert.Null(new AssemblyPlanner().Choose("electronic-circuit", [fluid], machines, []));
    }
}
