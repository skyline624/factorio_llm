using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConstructionSupplyPlannerTests
{
    private static readonly Dictionary<string, int> AssemblerKit = new()
    {
        ["assembling-machine-1"] = 1, ["inserter"] = 2, ["iron-chest"] = 2, ["small-electric-pole"] = 1
    };

    [Fact]
    public void OneKitCombinesSharedPlateDemandAndNativeCableAndPoleSurplus()
    {
        var plan = ConstructionSupplyPlanner.Plan(Catalog(), AssemblerKit, new Dictionary<string, long>())!;
        Assert.Equal(new Dictionary<string, int> { ["iron-plate"] = 46, ["copper-plate"] = 9, ["wood"] = 1 }, Materials(plan));
        Assert.Equal(AssemblerKit, plan.Equipment.ToDictionary(p => p.Item, p => p.TargetStock));
    }

    [Fact]
    public void SharedCarriedIngredientsAndFinishedPartsAreCountedOnlyOnce()
    {
        var carried = new Dictionary<string, long>
        {
            ["iron-plate"] = 8, ["copper-plate"] = 2, ["iron-gear-wheel"] = 5, ["copper-cable"] = 3,
            ["iron-chest"] = 1, ["small-electric-pole"] = 1
        };
        var plan = ConstructionSupplyPlanner.Plan(Catalog(), AssemblerKit, carried)!;
        // Targets include carried plates; only the missing twenty iron and four copper plates will be procured.
        Assert.Equal(new Dictionary<string, int> { ["iron-plate"] = 28, ["copper-plate"] = 6 }, Materials(plan));
        Assert.Equal(8, carried["iron-plate"]);
        Assert.Equal(1, carried["iron-chest"]);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 5)]
    public void ARequiredFurnaceCanBeUsedByADrillAndIsReplenishedAfterItsConsumer(int furnaceStock, int stones)
    {
        var needed = new Dictionary<string, int> { ["stone-furnace"] = 1, ["burner-mining-drill"] = 1 };
        var plan = ConstructionSupplyPlanner.Plan(Catalog(), needed,
            new Dictionary<string, long> { ["stone-furnace"] = furnaceStock })!;
        Assert.Equal(new Dictionary<string, int> { ["stone"] = stones, ["iron-plate"] = 9 }, Materials(plan));
        Assert.Equal(["burner-mining-drill", "stone-furnace"], plan.Equipment.Select(p => p.Item));
    }

    [Fact]
    public void ACarriedDrillDoesNotHideTheSteelFurnacesMachineProducedBrickRequirement()
    {
        var catalog = Catalog() with
        {
            Recipes = [
                new("electric-mining-drill", true, "crafting", 2,
                    [new("iron-plate", "item", 10)], [new("electric-mining-drill", "item", 1)], false),
                new("steel-furnace", true, "crafting", 3,
                    [new("steel-plate", "item", 8), new("stone-brick", "item", 10)], [new("steel-furnace", "item", 1)], false),
                new("stone-brick", true, "smelting", 3.2,
                    [new("stone", "item", 2)], [new("stone-brick", "item", 1)], true)]
        };
        var needed = new Dictionary<string, int> { ["electric-mining-drill"] = 1, ["steel-furnace"] = 1 };
        var plan = ConstructionSupplyPlanner.Plan(catalog, needed,
            new Dictionary<string, long> { ["electric-mining-drill"] = 1, ["steel-plate"] = 8 })!;
        Assert.Equal(new Dictionary<string, int> { ["stone-brick"] = 10 }, Materials(plan));
        Assert.Equal(needed, plan.Equipment.ToDictionary(p => p.Item, p => p.TargetStock));
    }

    [Fact]
    public void ObservedStoredGearsAreStockGoalsRatherThanInventedCarriedStockOrNewPlateDemand()
    {
        var plan = ConstructionSupplyPlanner.Plan(Catalog(), AssemblerKit, new Dictionary<string, long>(),
            new HashSet<string> { "iron-gear-wheel" })!;
        Assert.Equal(7, Materials(plan)["iron-gear-wheel"]);
        Assert.Equal(32, Materials(plan)["iron-plate"]);
        Assert.Equal(9, Materials(plan)["copper-plate"]);
    }

    [Fact]
    public void AlreadyCarriedKitRequestsNoMaterials()
    {
        var plan = ConstructionSupplyPlanner.Plan(Catalog(), AssemblerKit,
            AssemblerKit.ToDictionary(p => p.Key, p => (long)p.Value))!;
        Assert.Empty(plan.Materials);
    }

    [Fact]
    public void OversizedCombinedDemandFallsBackWithoutClippingTheKit()
    {
        Assert.Null(ConstructionSupplyPlanner.Plan(Catalog(), new Dictionary<string, int> { ["iron-chest"] = 126 },
            new Dictionary<string, long>()));
    }

    [Fact]
    public void CyclicIngredientsCannotExpandWithoutBound()
    {
        var catalog = Catalog() with { Recipes = [Recipe("a", "b", 1), Recipe("b", "a", 1)] };
        Assert.Null(ConstructionSupplyPlanner.Plan(catalog, new Dictionary<string, int> { ["a"] = 1 }, new Dictionary<string, long>()));
    }

    [Fact]
    public void DuplicateIngredientsAreSummedAndNativeMultiItemYieldIsShared()
    {
        var catalog = Catalog() with
        {
            Recipes = [new("parts", true, "crafting", .5,
                [new("iron-plate", "item", 2), new("iron-plate", "item", 3)], [new("part", "item", 2)], false)]
        };
        var plan = ConstructionSupplyPlanner.Plan(catalog, new Dictionary<string, int> { ["part"] = 3 }, new Dictionary<string, long>())!;
        Assert.Equal(new Dictionary<string, int> { ["iron-plate"] = 10 }, Materials(plan));
    }

    [Fact]
    public void AmbiguousRecipesAreDelegatedToTheOrdinaryExecutor()
    {
        var catalog = Catalog();
        catalog = catalog with { Recipes = [..catalog.Recipes, Recipe("alternative-gears", "copper-plate", 1, "iron-gear-wheel")] };
        var plan = ConstructionSupplyPlanner.Plan(catalog, AssemblerKit, new Dictionary<string, long>())!;
        Assert.Equal(7, Materials(plan)["iron-gear-wheel"]);
        Assert.Equal(32, Materials(plan)["iron-plate"]);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("probabilistic")]
    [InlineData("fluid")]
    public void NonExecutableHandcraftInputsAreNotExpandedAsGuaranteedMaterials(string change)
    {
        var catalog = Catalog();
        var chest = catalog.Recipes.Single(r => r.Name == "iron-chest");
        chest = change switch
        {
            "locked" => chest with { Enabled = false },
            "probabilistic" => chest with { Products = [new("iron-chest", "item", 1, Probability: .5)] },
            "fluid" => chest with { Ingredients = [new("water", "fluid", 5)] },
            _ => throw new ArgumentException(change)
        };
        catalog = catalog with { Recipes = catalog.Recipes.Where(r => r.Name != "iron-chest").Append(chest).ToArray() };
        var plan = ConstructionSupplyPlanner.Plan(catalog, new Dictionary<string, int> { ["iron-chest"] = 2 }, new Dictionary<string, long>())!;
        Assert.Equal(new Dictionary<string, int> { ["iron-chest"] = 2 }, Materials(plan));
    }

    internal static ProductionCatalog Catalog()
    {
        var catalog = Catalogs.Raw();
        var pole = catalog.Recipes.Single(r => r.Name == "small-electric-pole");
        return catalog with
        {
            Recipes = catalog.Recipes.Where(r => r.Name != pole.Name)
                .Append(pole with { Ingredients = [..pole.Ingredients, new("wood", "item", 1)] }).ToArray()
        };
    }
    private static NativeRecipe Recipe(string name, string input, int amount, string? output = null) =>
        new(name, true, "crafting", .5, [new(input, "item", amount)], [new(output ?? name, "item", 1)], false);
    private static Dictionary<string, int> Materials(ConstructionSupplyPlan plan) => plan.Materials.ToDictionary(p => p.Item, p => p.TargetStock);
}
