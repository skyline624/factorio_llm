using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AssemblyPlannerTests
{
    private static readonly NativeRecipe Circuit = new("electronic-circuit", true, "crafting", .5,
        [new("iron-plate", "item", 1), new("copper-cable", "item", 3)], [new("electronic-circuit", "item", 1)], false);

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
