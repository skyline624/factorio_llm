using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionPlannerTests
{
    [Fact]
    public void SciencePackLotAllowsNativeDurationAndQueueTransitions()
    {
        var recipe = Recipe("science", [Item("iron-plate", 1)], [Item("science", 1)]) with { EnergySeconds = 5 };
        var step = Next("science", 120, new() { ["iron-plate"] = 120 }, Catalog() with { Recipes = [recipe] });
        Assert.Equal("craft", step.Kind);
        Assert.Equal(120, step.Quantity);
        Assert.InRange(HandcraftTiming.DeadlineTicks(recipe, step.Quantity), 36300, HandcraftTiming.MaximumTicks);
    }

    [Fact]
    public void SlowCraftsSplitWithinNativeOperationHorizonAndPreserveRemainingGoal()
    {
        var recipe = Recipe("slow", [Item("iron-plate", 1)], [Item("slow", 1)]) with { EnergySeconds = 120 };
        var catalog = Catalog() with { Recipes = [recipe] };
        var step = Next("slow", 100, new() { ["iron-plate"] = 100 }, catalog);
        Assert.Equal("craft", step.Kind);
        Assert.InRange(step.Quantity, 1, 99);
        Assert.InRange(HandcraftTiming.DeadlineTicks(recipe, step.Quantity), 1, HandcraftTiming.MaximumTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => HandcraftTiming.DeadlineTicks(recipe, step.Quantity + 1));
        var remaining = Next("slow", 100, new() { ["slow"] = 99, ["iron-plate"] = 1 }, catalog);
        Assert.Equal("craft", remaining.Kind);
        Assert.Equal(1, remaining.Quantity);
    }

    [Theory]
    [InlineData(0, 0, "automate", "plate", 1000)]
    [InlineData(0, 1000, "craft", "gear", 500)]
    [InlineData(500, 0, "automate", "plate", 1000)]
    [InlineData(500, 1000, "craft", "gear", 500)]
    public void LargeCraftGoalUsesExecutableIntermediateLotsWithoutReducingTheFinalTarget(
        long gears, long plates, string kind, string item, int quantity)
    {
        var (map, catalog) = SmeltingPlannerTests.Setup(installed: true);
        catalog = catalog with { Recipes = [..catalog.Recipes, Recipe("gear", [Item("plate", 2)], [Item("gear", 1)])],
            HandCategories = new Dictionary<string, bool> { ["crafting"] = true } };
        var step = new ProductionPlanner().Next("gear", 1000,
            new Dictionary<string, long> { ["gear"] = gears, ["plate"] = plates }, catalog, map,
            [new("receiver", "furnace", "smelt-plate"), new("installed", "drill", null)]);
        Assert.Equal(kind, step.Kind);
        Assert.Equal(item, step.Item);
        Assert.Equal(quantity, step.Quantity);
    }

    [Fact]
    public void LargeCraftLotSumsDuplicateIngredientsBeforeBoundingTheBatch()
    {
        var catalog = Catalog() with { Recipes = [Recipe("part", [Item("iron-plate", 2), Item("iron-plate", 1)], [Item("part", 1)])] };
        var step = Next("part", 1000, new() { ["iron-plate"] = 999 }, catalog);
        Assert.Equal("craft", step.Kind);
        Assert.Equal(333, step.Quantity);
    }

    [Theory]
    [InlineData(100, 75)]
    [InlineData(10, 10)]
    public void MiningForFurnaceUsesNativeInputStackInsteadOfSixteenItemTrips(int stackSize, int expected)
    {
        var catalog = Catalog() with { Items = new Dictionary<string, NativeItem> { ["iron-ore"] = new(0, stackSize) } };
        var step = Next("iron-plate", 75, new(), catalog);
        Assert.Equal("unavailable", step.Kind);
        Assert.Equal("iron-ore", step.Item);
        Assert.Equal(expected, step.Quantity);
    }

    [Fact]
    public void FurnaceBatchAccountsForMultipleIngredientUnitsPerCycle()
    {
        var recipe = Recipe("steel", [Item("iron-plate", 5)], [Item("steel", 1)], "smelting");
        var catalog = Catalog() with
        {
            Recipes = [recipe],
            Items = new Dictionary<string, NativeItem> { ["iron-plate"] = new(0, 100) }
        };
        var step = Next("steel", 30, new() { ["iron-plate"] = 100 }, catalog);
        Assert.Equal("smelt", step.Kind);
        Assert.Equal(20, step.Quantity);
    }

    [Fact]
    public void Existing_stock_satisfies_target_without_manufacturing_it_again()
    {
        Assert.Equal("satisfied", Next("gear", 5, new() { ["gear"] = 5 }).Kind);
    }

    [Fact]
    public void Dependency_quantities_use_native_recipe_yield_and_existing_stock()
    {
        ProductionStep step = Next("belt", 5, new() { ["iron-plate"] = 8 });
        Assert.Equal("craft", step.Kind);
        Assert.Equal("gear", step.Item);
        Assert.Equal(3, step.Quantity); // Three two-belt batches need three gears.
        step = Next("belt", 5, new() { ["iron-plate"] = 3, ["gear"] = 3 });
        Assert.Equal("belt", step.Item);
        Assert.Equal(3, step.Quantity);
    }

    [Fact]
    public void Ingredient_duplicates_are_summed_before_inventory_comparison()
    {
        ProductionCatalog catalog = Catalog() with { Recipes = [Recipe("double", [Item("gear", 1), Item("gear", 2)], [Item("double", 1)])] };
        ProductionStep step = Next("double", 1, new() { ["gear"] = 2 }, catalog);
        Assert.Equal("unsupported", step.Kind);
        Assert.Equal("gear", step.Item);
    }

    [Fact]
    public void Missing_visible_source_requires_exploration_without_inventing_a_location()
    {
        ProductionStep step = Next("iron-plate", 20, new() { ["iron-plate"] = 8 });
        Assert.Equal("unavailable", step.Kind);
        Assert.Equal("iron-ore", step.Item);
        Assert.Equal(12, step.Quantity);
        Assert.Null(step.Source);
    }

    [Fact]
    public void Mining_selection_uses_native_product_mapping_instead_of_name_guessing()
    {
        SpatialSnapshot map = SpatialPlannerTests.Map([]) with
        {
            Entities = [new("observed-tree", "tree-variant", new(4, 5), new(new(3.5, 4.5), new(4.5, 5.5)), 0, "neutral")]
        };
        ProductionStep step = new ProductionPlanner().Next("wood", 3, new Dictionary<string, long>(), Catalog(), map, []);
        Assert.Equal("mine", step.Kind);
        Assert.Equal("observed-tree", step.Source!.Id);
        Assert.Equal(3, step.Quantity);
    }

    [Fact]
    public void Furnace_recipe_is_selected_only_after_ingredients_are_available()
    {
        ProductionStep step = Next("iron-plate", 20, new() { ["iron-plate"] = 8, ["iron-ore"] = 12 });
        Assert.Equal("smelt", step.Kind);
        Assert.Equal(12, step.Quantity);
    }

    [Fact]
    public void Dependency_cycles_are_bounded_and_locked_recipes_are_not_executable()
    {
        ProductionCatalog catalog = Catalog() with
        {
            Recipes = [
            Recipe("a", [Item("b", 1)], [Item("a", 1)]),
            Recipe("b", [Item("a", 1)], [Item("b", 1)])]
        };
        Assert.Equal("unsupported", Next("a", 1, new(), catalog).Kind);
        catalog = catalog with { Recipes = [Recipe("a", [], [Item("a", 1)]) with { Enabled = false }] };
        Assert.Equal("unsupported", Next("a", 1, new(), catalog).Kind);
    }

    [Fact]
    public void Probabilistic_or_fluid_results_are_not_promised_as_deterministic_solid_stock()
    {
        foreach (NativeMaterial output in new[] { Item("a", 1) with { Probability = 0.5 }, new NativeMaterial("a", "fluid", 10) })
        {
            ProductionCatalog catalog = Catalog() with { Recipes = [Recipe("a", [], [output])] };
            Assert.Equal("unsupported", Next("a", 1, new(), catalog).Kind);
        }
    }

    [Fact]
    public void MissingFurnaceIsCraftedFromItsNativeRecipeBeforeSmelting()
    {
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["stone"] = 5, ["iron-ore"] = 4 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]), []);
        Assert.Equal("craft", step.Kind);
        Assert.Equal("furnace", step.Item);
        Assert.Equal(1, step.Quantity);
    }

    [Fact]
    public void MissingMachineIngredientRequiresExplorationRatherThanInventedStock()
    {
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["iron-ore"] = 4 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]), []);
        Assert.Equal("unavailable", step.Kind);
        Assert.Equal("stone", step.Item);
        Assert.Equal(5, step.Quantity);
    }

    [Fact]
    public void CarriedMachineIsInstalledBeforeRequestingItsIngredients()
    {
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["furnace"] = 1 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]), []);
        Assert.Equal("build", step.Kind);
        Assert.Equal("furnace", step.Item);
        Assert.Equal(1, step.Quantity);
    }

    [Fact]
    public void KnownCompatibleMachineOutsideLocalMapDoesNotCauseDuplicateConstruction()
    {
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["furnace"] = 1, ["iron-ore"] = 4 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]),
            [new("distant", "furnace", "iron-plate")]);
        Assert.Equal("smelt", step.Kind);
    }

    [Fact]
    public void MachineBusyWithAnotherRecipeDoesNotSatisfyThePrerequisite()
    {
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["furnace"] = 1, ["iron-ore"] = 4 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]),
            [new("occupied", "furnace", "other-recipe")]);
        Assert.Equal("build", step.Kind);
    }

    [Fact]
    public void MachineWhoseRecipeDependsOnItsOwnProductionIsNotAnExecutablePrerequisite()
    {
        var catalog = Catalog() with
        {
            Recipes = [.. Catalog().Recipes,
            Recipe("furnace", [Item("iron-plate", 5)], [Item("furnace", 1)])]
        };
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
        { ["iron-ore"] = 4 }, catalog, SpatialPlannerTests.Map([]), []);
        Assert.Equal("unsupported", step.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdleRecipeWithForeignCompartmentContentsStillRequiresAnotherMachine(bool inInput)
    {
        var contents = new Dictionary<string, long> { ["foreign-item"] = 6 };
        var step = new ProductionPlanner().Next("iron-plate", 4, new Dictionary<string, long>
            { ["furnace"] = 1, ["iron-ore"] = 4 }, WithFurnaceRecipe(), SpatialPlannerTests.Map([]),
            [new("occupied", "furnace", null, inInput ? contents : null, inInput ? null : contents)]);
        Assert.Equal("build", step.Kind);
    }

    [Fact]
    public void IntermediatePlateRequirementUsesInstalledExtractionWithTotalStockTarget()
    {
        var (map, catalog) = SmeltingPlannerTests.Setup(installed: true);
        catalog = catalog with
        {
            Recipes = [.. catalog.Recipes, Recipe("gear", [Item("plate", 2)], [Item("gear", 1)])],
            HandCategories = new Dictionary<string, bool> { ["crafting"] = true }
        };
        var step = new ProductionPlanner().Next("gear", 3, new Dictionary<string, long> { ["plate"] = 1 }, catalog, map,
            [new("receiver", "furnace", "smelt-plate"), new("installed", "drill", null)]);
        Assert.Equal("automate", step.Kind);
        Assert.Equal("plate", step.Item);
        Assert.Equal(6, step.Quantity); // Total stock, not five missing plates or three gear batches.
    }

    private static ProductionCatalog WithFurnaceRecipe() => Catalog() with
    {
        Recipes = [.. Catalog().Recipes, Recipe("furnace", [Item("stone", 5)], [Item("furnace", 1)])],
        Mining = new Dictionary<string, NativeMaterial[]>(Catalog().Mining) { ["stone-patch"] = [Item("stone", 1)] }
    };

    private static ProductionStep Next(string item, int quantity, Dictionary<string, long> inventory, ProductionCatalog? catalog = null) =>
        new ProductionPlanner().Next(item, quantity, inventory, catalog ?? Catalog(), SpatialPlannerTests.Map([]),
            [new("known-furnace", "furnace", null)]);
    private static NativeMaterial Item(string name, int amount) => new(name, "item", amount);
    private static NativeRecipe Recipe(string name, NativeMaterial[] ingredients, NativeMaterial[] products, string category = "crafting") =>
        new(name, true, category, 1, ingredients, products, false);
    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 2), 100,
        [Recipe("gear", [Item("iron-plate", 2)], [Item("gear", 1)]),
         Recipe("belt", [Item("gear", 1), Item("iron-plate", 1)], [Item("belt", 2)]),
         Recipe("iron-plate", [Item("iron-ore", 1)], [Item("iron-plate", 1)], "smelting")],
        new Dictionary<string, NativeItem> { ["iron-ore"] = new(0, 100), ["iron-plate"] = new(0, 100) },
        new Dictionary<string, NativeMaterial[]> { ["iron-ore"] = [Item("iron-ore", 1)], ["tree-variant"] = [Item("wood", 4)] },
        new Dictionary<string, NativeFurnace>
        {
            ["furnace"] = new("furnace", new Dictionary<string, bool> { ["smelting"] = true },
            new Dictionary<string, bool> { ["chemical"] = true }, 1)
        },
        new Dictionary<string, bool> { ["crafting"] = true });
}
