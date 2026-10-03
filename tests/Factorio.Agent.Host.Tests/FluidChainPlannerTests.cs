using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidChainPlannerTests
{
    [Fact]
    public void PlasticChainsAChemicalStageToRefiningAndCrudeExtraction()
    {
        // 30 plastic per minute: 15 crafts, 300 gas, 6.67 refinery cycles, 666.7 crude; coal stays raw for logistics.
        var plan = OilCatalogs.Plan(OilCatalogs.Oil(), "plastic-bar", 30)!;
        Assert.Equal(["basic-oil-processing", "plastic-bar"], plan.Stages.Select(s => s.Recipe));
        var refinery = plan.Stages[0];
        var chemical = plan.Stages[1];
        Assert.Equal(("petroleum-gas", "oil-refinery", 1), (refinery.Item, refinery.MachineItem, refinery.Machines));
        Assert.Equal(300 / 45.0, refinery.CraftsPerMinute, 6);
        Assert.Equal(("plastic-bar", "chemical-plant", 1), (chemical.Item, chemical.MachineItem, chemical.Machines));
        Assert.Equal(15, chemical.CraftsPerMinute, 6);
        var crude = Assert.Single(plan.Sources);
        Assert.Equal(("crude-oil", "crude-oil"), (crude.Fluid, crude.Resource));
        Assert.Equal(300 / 45.0 * 100, crude.UnitsPerMinute, 6);
        Assert.Equal(15, plan.RawPerMinute["coal"], 6);
    }

    [Fact]
    public void InserterThroughputBoundsChemicalCellsBeforeCraftingSpeed()
    {
        // Two plastic bars leave per craft: one basic inserter carries 48 per minute, so 60 plastic need two cells.
        var plan = OilCatalogs.Plan(OilCatalogs.Oil(), "plastic-bar", 60)!;
        Assert.Equal(2, plan.Stages.Single(s => s.Recipe == "plastic-bar").Machines);
    }

    [Fact]
    public void DedicatedAndSharedFluidPlansKeepDemandBeyondEightMachines()
    {
        var catalog = OilCatalogs.Oil();
        var dedicated = OilCatalogs.Plan(catalog, "plastic-bar", 498)!;
        var shared = AutomationPlanner.Plan(catalog, "plastic-bar", 498, FactoryDirector.MachineItems(catalog),
            fluidMachineItems: FluidChainDirector.Machines(catalog));
        foreach (var stages in new[] { dedicated.Stages, shared.Stages })
        {
            Assert.Equal(11, stages.Single(s => s.Recipe == "plastic-bar").Machines);
            Assert.Equal(10, stages.Single(s => s.Recipe == "basic-oil-processing").Machines);
            Assert.All(stages, s => Assert.True(s.Machines * AutomationPlanner.CellCraftsPerMinute(catalog,
                catalog.Recipes.Single(r => r.Name == s.Recipe), s.MachineItem) >= s.CraftsPerMinute));
        }
    }

    [Fact]
    public void SulfurDrawsWaterFromTerrainAndSizesRefineriesByCraftingSpeed()
    {
        // 60 sulfur: 30 crafts, 900 gas = 20 refinery cycles against 12 per refinery and minute.
        var plan = OilCatalogs.Plan(OilCatalogs.Oil(), "sulfur", 60)!;
        Assert.Equal(2, plan.Stages.Single(s => s.Recipe == "basic-oil-processing").Machines);
        var water = plan.Sources.Single(s => s.Fluid == "water");
        Assert.Null(water.Resource);
        Assert.Equal(900, water.UnitsPerMinute, 6);
        Assert.Empty(plan.RawPerMinute);
    }

    [Fact]
    public void SulfuricAcidChainsSulfurAsASolidStageDeliveredByTheActor()
    {
        var plan = OilCatalogs.Plan(OilCatalogs.Oil(), "sulfuric-acid", 500)!;
        Assert.Equal(["basic-oil-processing", "sulfur", "sulfuric-acid"], plan.Stages.Select(s => s.Recipe));
        Assert.Equal(25, plan.Stages.Single(s => s.Recipe == "sulfur").CraftsPerMinute, 6);
        Assert.Equal(10, plan.RawPerMinute["iron-plate"], 6);
        Assert.Equal(1000 + 750, plan.Sources.Single(s => s.Fluid == "water").UnitsPerMinute, 6);
    }

    [Fact]
    public void RecipesWhoseFluidsCannotBeSuppliedAreNotChosen()
    {
        // Cracking needs light oil, which only multi-product processing makes: basic processing remains the supplier.
        var catalog = OilCatalogs.Oil();
        catalog = catalog with { Recipes = [.. catalog.Recipes, new("light-oil-cracking", true, "chemistry", 2,
            [OilCatalogs.Fluid("light-oil", 40), OilCatalogs.Fluid("water", 30)], [OilCatalogs.Fluid("petroleum-gas", 20)], false)] };
        Assert.Equal("basic-oil-processing", OilCatalogs.Choose(catalog, "petroleum-gas")!.Value.Recipe.Name);
    }

    [Theory]
    [InlineData("processing-unit")]
    [InlineData("battery")]
    public void AFluidMadeFromDeliveredSolidsFeedsItsConsumerAfterPriming(string target)
    {
        // recipe.lua: processing units and batteries consume sulfuric acid, which a chemical cell makes from sulfur and iron.
        var catalog = OilCatalogs.Advanced();
        Assert.Equal(target, OilCatalogs.Choose(catalog, target)!.Value.Recipe.Name);
        var plan = OilCatalogs.Plan(catalog, target, 10)!;
        Assert.Equal(["basic-oil-processing", "sulfur", "sulfuric-acid", target], plan.Stages.Select(s => s.Recipe));
        Assert.Equal(target == "battery" ? 4 : 1, plan.Stages.Single(s => s.Recipe == "sulfuric-acid").CraftsPerMinute, 6);
        Assert.Equal(target == "battery" ? 14 : 1, plan.RawPerMinute["iron-plate"], 6);
        Assert.Equal(target == "battery" ? 10 : 2.5, plan.Stages.Single(s => s.Recipe == "sulfur").CraftsPerMinute, 6);
        Assert.Equal("sulfuric-acid", OilCatalogs.Choose(catalog, "sulfuric-acid")!.Value.Recipe.Name);
    }

    [Fact]
    public void AnUnresearchedAcidRecipeCannotSupplyBatteries()
    {
        var catalog = OilCatalogs.Advanced();
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "sulfuric-acid" ? r with { Enabled = false } : r).ToArray() };
        Assert.Null(OilCatalogs.Choose(catalog, "battery"));
    }

    [Fact]
    public void AFluidSupplierCycleCannotAuthorizeAConsumer()
    {
        var catalog = OilCatalogs.Advanced();
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "sulfuric-acid"
            ? r with { Ingredients = [OilCatalogs.Fluid("sulfuric-acid", 1)] } : r).ToArray() };
        Assert.Null(OilCatalogs.Choose(catalog, "processing-unit"));
    }

    [Fact]
    public void OnlyObtainableMachinesServeAChain()
    {
        // assembling-machine-3 is faster but its recipe is not researched: concrete goes to the enabled assembling-machine-2.
        var catalog = OilCatalogs.Advanced();
        Assert.Equal(["assembling-machine-1", "assembling-machine-2", "chemical-plant", "oil-refinery"], FluidChainDirector.Machines(catalog).Order());
        Assert.Equal("assembling-machine-2", OilCatalogs.Choose(catalog, "concrete")!.Value.MachineItem);
        var carried = new Dictionary<string, long> { ["assembling-machine-3"] = 1 };
        Assert.Equal("assembling-machine-3", FluidChainPlanner.Choose(catalog, "concrete", FluidChainDirector.Machines(catalog, carried))!.Value.MachineItem);
        Assert.Null(FluidChainPlanner.Choose(catalog, "plastic-bar", new HashSet<string> { "oil-refinery" }));
    }

    [Theory]
    [InlineData("iron-gear-wheel")]
    [InlineData("iron-plate")]
    [InlineData("disabled-plastic")]
    [InlineData("no-chemical-plant")]
    public void OnlyEnabledFluidRecipesWithAFluidMachineAreChains(string target)
    {
        var catalog = OilCatalogs.Oil();
        if (target == "disabled-plastic")
            catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "plastic-bar" ? r with { Enabled = false } : r).ToArray() };
        if (target == "no-chemical-plant")
            catalog = catalog with { Assemblers = catalog.Assemblers!.Where(p => p.Key != "chemical-plant").ToDictionary(p => p.Key, p => p.Value) };
        string item = target is "disabled-plastic" or "no-chemical-plant" ? "plastic-bar" : target;
        Assert.Null(OilCatalogs.Plan(catalog, item, 10));
    }

    [Theory]
    [InlineData("water", true)]
    [InlineData("crude-oil", false)]
    [InlineData("petroleum-gas", false)]
    public void OnlyFluidsOfNativeTilesComeFromTerrain(string fluid, bool terrain) =>
        Assert.Equal(terrain, FluidChainPlanner.Terrain(OilCatalogs.Oil(), fluid));

    [Fact]
    public void BarrelEmptyingDoesNotHideTheTerrainWaterOfSulfur()
    {
        // Live catalog of 30/09: recipe.lua empties water barrels into water, yet offshore pumps still draw it from water tiles.
        var catalog = OilCatalogs.Oil();
        catalog = catalog with { Recipes = [.. catalog.Recipes, new("empty-water-barrel", true, "crafting-with-fluid", 0.2,
            [new("water-barrel", "item", 1)], [OilCatalogs.Fluid("water", 50), new("barrel", "item", 1)], false)] };
        Assert.Equal("sulfur", OilCatalogs.Choose(catalog, "sulfur")!.Value.Recipe.Name);
        Assert.Null(OilCatalogs.Choose(catalog with { TerrainFluids = null }, "sulfur"));
    }

    [Theory]
    [InlineData(0, 1, 0, 0, 1200, true, true)]
    [InlineData(0, 1, 1, 600, 1200, false, true)]
    [InlineData(1, 1, 1, 600, 1200, true, true)]
    [InlineData(1, 1, 1, 1200, 1200, false, false)]
    [InlineData(2, 2, 2, 400, 300, false, false)]
    [InlineData(1, 2, 1, 1200, 600, true, true)]
    public void ExtractorsAndTheirMachinesAreBuiltInPairs(int machines, int wanted, int extractors, double capacity, double demand,
        bool extractor, bool machine) =>
        Assert.Equal((extractor, machine), FluidChainDirector.NextPair(machines, wanted, extractors, capacity, demand));

    [Theory]
    [InlineData(300000, 600)]
    [InlineData(60000, 120)]
    [InlineData(150000, 300)]
    public void PumpjackRateFollowsTheNativeDepositYield(double amount, double perMinute)
    {
        // resources.lua: crude-oil normal 300000, mining_time 1, 10 units per cycle; mining-drill.lua: pumpjack speed 1.
        var pumpjack = new EntityGeometry("pumpjack", "mining-drill", new(new(-1.2, -1.2), new(1.2, 1.2)),
            new(["object"], false, false, false), 3, 3, MiningSpeed: 1, IsElectric: true);
        var crude = new EntityGeometry("crude-oil", "resource", new(new(-1.4, -1.4), new(1.4, 1.4)), new(["resource"], false, false, false),
            1, 1, ResourceCategory: "basic-fluid", MiningTime: 1, NormalResourceAmount: 300000, InfiniteResource: true);
        Assert.Equal(perMinute, FluidChainPlanner.ExtractorPerMinute(pumpjack, crude, amount, OilCatalogs.Fluid("crude-oil", 10)), 6);
    }
}

internal static class OilCatalogs
{
    public static NativeMaterial Fluid(string name, double amount) => new(name, "fluid", amount);
    private static NativeMaterial Item(string name, double amount) => new(name, "item", amount);
    private static NativeItem Placed(string name, string type, int stack = 50) => new(0, stack, PlaceEntity: name, PlaceEntityType: type);

    /// <summary>The chain with the machines the catalog enables, as the director plans it.</summary>
    public static FluidChainPlan? Plan(ProductionCatalog catalog, string item, double perMinute) =>
        FluidChainPlanner.Plan(catalog, item, perMinute, FluidChainDirector.Machines(catalog));

    public static (NativeRecipe Recipe, string MachineItem)? Choose(ProductionCatalog catalog, string item) =>
        FluidChainPlanner.Choose(catalog, item, FluidChainDirector.Machines(catalog));

    /// <summary>Raw catalog plus base-game oil processing: recipe.lua amounts, entities.lua crafting speeds.</summary>
    public static ProductionCatalog Oil()
    {
        var raw = Catalogs.Raw();
        return raw with
        {
            Recipes = [.. raw.Recipes,
                // Unlocked by oil-processing in technology.lua; ingredients are irrelevant here.
                new("oil-refinery", true, "crafting", 8, [Item("steel-plate", 15)], [Item("oil-refinery", 1)], false),
                new("chemical-plant", true, "crafting", 5, [Item("steel-plate", 5)], [Item("chemical-plant", 1)], false),
                new("basic-oil-processing", true, "oil-processing", 5, [Fluid("crude-oil", 100)], [Fluid("petroleum-gas", 45)], false),
                new("advanced-oil-processing", true, "oil-processing", 5, [Fluid("water", 50), Fluid("crude-oil", 100)],
                    [Fluid("heavy-oil", 25), Fluid("light-oil", 45), Fluid("petroleum-gas", 55)], false),
                new("plastic-bar", true, "chemistry", 1, [Fluid("petroleum-gas", 20), Item("coal", 1)], [Item("plastic-bar", 2)], false),
                new("sulfur", true, "chemistry", 1, [Fluid("water", 30), Fluid("petroleum-gas", 30)], [Item("sulfur", 2)], false),
                new("sulfuric-acid", true, "chemistry", 1, [Item("sulfur", 5), Item("iron-plate", 1), Fluid("water", 100)],
                    [Fluid("sulfuric-acid", 50)], false)],
            Items = new Dictionary<string, NativeItem>(raw.Items)
            {
                ["plastic-bar"] = new(0, 100), ["sulfur"] = new(0, 50), ["pipe"] = Placed("pipe", "pipe", 100),
                ["pumpjack"] = Placed("pumpjack", "mining-drill", 20), ["oil-refinery"] = Placed("oil-refinery", "assembling-machine", 10),
                ["chemical-plant"] = Placed("chemical-plant", "assembling-machine", 10), ["offshore-pump"] = Placed("offshore-pump", "offshore-pump", 20)
            },
            Mining = new Dictionary<string, NativeMaterial[]>(raw.Mining) { ["crude-oil"] = [Fluid("crude-oil", 10)] },
            TerrainFluids = ["water"],
            Assemblers = new Dictionary<string, NativeAssembler>(raw.Assemblers!)
            {
                ["oil-refinery"] = new("oil-refinery", new Dictionary<string, bool> { ["oil-processing"] = true }, 1, 7000, 255,
                    FluidInputCount: 2, FluidOutputCount: 3),
                ["chemical-plant"] = new("chemical-plant", new Dictionary<string, bool> { ["chemistry"] = true }, 1, 3500, 255,
                    FluidInputCount: 2, FluidOutputCount: 2)
            }
        };
    }

    /// <summary>
    /// Oil catalog plus recipe.lua consumers of sulfuric acid and water, with assembling-machine-2 researched and
    /// assembling-machine-3 not: entities.lua gives both one fluid input and one fluid output box.
    /// </summary>
    public static ProductionCatalog Advanced()
    {
        var oil = Oil();
        var fluidCrafting = new Dictionary<string, bool> { ["crafting"] = true, ["crafting-with-fluid"] = true };
        return oil with
        {
            Recipes = [.. oil.Recipes,
                new("assembling-machine-2", true, "crafting", 0.5, [Item("steel-plate", 2)], [Item("assembling-machine-2", 1)], false),
                new("assembling-machine-3", false, "crafting", 0.5, [Item("steel-plate", 2)], [Item("assembling-machine-3", 1)], false),
                new("processing-unit", true, "crafting-with-fluid", 10, [Item("electronic-circuit", 20), Item("advanced-circuit", 2),
                    Fluid("sulfuric-acid", 5)], [Item("processing-unit", 1)], false),
                new("battery", true, "chemistry", 4, [Fluid("sulfuric-acid", 20), Item("iron-plate", 1), Item("copper-plate", 1)],
                    [Item("battery", 1)], false),
                new("concrete", true, "crafting-with-fluid", 10, [Item("stone-brick", 5), Item("iron-ore", 1), Fluid("water", 100)],
                    [Item("concrete", 10)], false)],
            Items = new Dictionary<string, NativeItem>(oil.Items)
            {
                ["processing-unit"] = new(0, 100), ["battery"] = new(0, 200), ["concrete"] = new(0, 100), ["advanced-circuit"] = new(0, 200),
                ["assembling-machine-2"] = Placed("assembling-machine-2", "assembling-machine"),
                ["assembling-machine-3"] = Placed("assembling-machine-3", "assembling-machine")
            },
            Assemblers = new Dictionary<string, NativeAssembler>(oil.Assemblers!)
            {
                ["assembling-machine-2"] = new("assembling-machine-2", fluidCrafting, 0.75, 2500, 255, FluidInputCount: 1, FluidOutputCount: 1),
                ["assembling-machine-3"] = new("assembling-machine-3", fluidCrafting, 1.25, 6250, 255, FluidInputCount: 1, FluidOutputCount: 1)
            }
        };
    }
}
