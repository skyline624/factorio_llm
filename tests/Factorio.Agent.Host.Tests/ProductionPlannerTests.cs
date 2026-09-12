using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionPlannerTests
{
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
        ProductionStep step = new ProductionPlanner().Next("wood", 3, new Dictionary<string, long>(), Catalog(), map);
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
        ProductionCatalog catalog = Catalog() with { Recipes = [
            Recipe("a", [Item("b", 1)], [Item("a", 1)]),
            Recipe("b", [Item("a", 1)], [Item("b", 1)])] };
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

    private static ProductionStep Next(string item, int quantity, Dictionary<string, long> inventory, ProductionCatalog? catalog = null) =>
        new ProductionPlanner().Next(item, quantity, inventory, catalog ?? Catalog(), SpatialPlannerTests.Map([]));
    private static NativeMaterial Item(string name, int amount) => new(name, "item", amount);
    private static NativeRecipe Recipe(string name, NativeMaterial[] ingredients, NativeMaterial[] products, string category = "crafting") =>
        new(name, true, category, 1, ingredients, products, false);
    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 2), 100,
        [Recipe("gear", [Item("iron-plate", 2)], [Item("gear", 1)]),
         Recipe("belt", [Item("gear", 1), Item("iron-plate", 1)], [Item("belt", 2)]),
         Recipe("iron-plate", [Item("iron-ore", 1)], [Item("iron-plate", 1)], "smelting")],
        new Dictionary<string, NativeItem>(),
        new Dictionary<string, NativeMaterial[]> { ["iron-ore"] = [Item("iron-ore", 1)], ["tree-variant"] = [Item("wood", 4)] },
        new Dictionary<string, NativeFurnace> { ["furnace"] = new("furnace", new Dictionary<string, bool> { ["smelting"] = true },
            new Dictionary<string, bool> { ["chemical"] = true }, 1) },
        new Dictionary<string, bool> { ["crafting"] = true });
}
