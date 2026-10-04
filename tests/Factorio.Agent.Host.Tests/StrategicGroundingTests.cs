using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicGroundingTests
{
    [Fact]
    public void AvailableResearchNamesTheRecipesItUnlocks()
    {
        // Campaign 2026-10-01 (seed 20261002): the planner never saw that military research brings a better gun.
        var effects = Protocol.ToElement(new object[]
        {
            new { type = "unlock-recipe", recipe = "submachine-gun" },
            new { type = "gun-speed", ammo_category = "bullet", modifier = 0.1 },
            new { type = "unlock-recipe", recipe = "shotgun" }
        });
        Assert.Equal(["submachine-gun", "shotgun"], StrategicProductionController.Unlocks(effects));
        Assert.Empty(StrategicProductionController.Unlocks(null));
    }

    [Fact]
    public void DefenseMeansInstalledNativeTurretsRatherThanCarriedItemStock()
    {
        var catalog = Catalog() with { Items = new Dictionary<string, NativeItem>(Catalog().Items)
            { ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret") } };
        var goal = Goal() with { Category = GoalCategory.Defense, Target = "gun-turret", Quantity = 8 };
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, stock: Turrets));
        catalog = catalog with { Turrets = new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) } };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog, stock: Turrets));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 0 }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 33 }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 1.5m }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Unit = GoalUnit.Completion }, "observation", catalog));
    }

    [Fact]
    public void TurretsMustBeStockedOrCraftableBeforeTheyCanBeInstalled()
    {
        // Campaign 2026-09-30 (seed 20261002): a turret goal before gun-turret research failed in production instead of
        // being refused, and cost one of the five failures the campaign tolerates.
        var catalog = Catalog() with
        {
            Items = new Dictionary<string, NativeItem>(Catalog().Items) { ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret") },
            Turrets = new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) }
        };
        var goal = Goal() with { Category = GoalCategory.Defense, Target = "gun-turret", Quantity = 4 };
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, stock: new Dictionary<string, long>()));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog,
            stock: new Dictionary<string, long> { ["gun-turret"] = 3 })); // Installed and stocked turrets must cover the goal.
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog,
            stock: new Dictionary<string, long> { ["gun-turret"] = 4 }));
        var crafted = catalog with { Recipes = [new("gun-turret", true, "crafting", 8, [new("iron-plate", "item", 20)], [new("gun-turret", "item", 1)], false)] };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", crafted, stock: new Dictionary<string, long>()));
    }

    [Fact]
    public void PerimeterWallsNeedTheFactoryRegistryASupportedTurretAndASingleCompletion()
    {
        var catalog = Catalog() with
        {
            Items = new Dictionary<string, NativeItem>(Catalog().Items)
            {
                ["stone-wall"] = new(0, 100, PlaceEntity: "stone-wall", PlaceEntityType: "wall"),
                ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret")
            },
            Turrets = new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) }
        };
        var goal = Goal() with { Category = GoalCategory.Defense, Target = "stone-wall", Unit = GoalUnit.Completion, Quantity = 1 };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog, factory: true, stock: WallsAndTurrets));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, factory: true, stock: Turrets));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Unit = GoalUnit.Items, Quantity = 40 }, "observation", catalog, factory: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog with { Turrets = null }, factory: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", catalog, factory: true));
        // Turret deployment keeps its item semantics next to perimeter walls.
        Assert.Null(StrategicProductionController.GroundingFailure(goal with { Target = "gun-turret", Unit = GoalUnit.Items, Quantity = 4 },
            "observation", catalog, factory: true, stock: WallsAndTurrets));
    }

    [Fact]
    public void LaunchRequiresNativeSiloAndSingleCompletion()
    {
        var catalog = Catalog() with { Items = new Dictionary<string, NativeItem>(Catalog().Items)
            { ["rocket-silo"] = new(0, 1, PlaceEntity: "rocket-silo", PlaceEntityType: "rocket-silo") } };
        var goal = Goal() with { Category = GoalCategory.Launch, Unit = GoalUnit.Completion, Quantity = 1, Target = "rocket-silo" };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Unit = GoalUnit.Items }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 2 }, "observation", catalog));
    }

    [Fact]
    public void Exact_native_stock_goal_can_be_grounded_without_model_coordinates()
    {
        Assert.Null(StrategicProductionController.GroundingFailure(Goal(), "observation", Catalog()));
    }

    [Fact]
    public void Novel_goal_remains_a_proposal_instead_of_being_reinterpreted_as_production()
    {
        GoalProposal goal = Goal() with { Category = GoalCategory.Exploration, Unit = GoalUnit.Completion,
            Target = "find a defensible expansion area", Quantity = 1 };
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", Catalog()));
    }

    [Theory]
    [InlineData("crude-oil", GoalUnit.Completion, 1, true)]
    [InlineData("iron-ore", GoalUnit.Completion, 1, true)]
    [InlineData("crude oil", GoalUnit.Completion, 1, false)]
    [InlineData("iron-plate", GoalUnit.Completion, 1, false)]
    [InlineData("tree", GoalUnit.Completion, 1, false)]
    [InlineData("crude-oil", GoalUnit.Items, 1, false)]
    [InlineData("crude-oil", GoalUnit.Completion, 2, false)]
    public void ResourceExplorationUsesExactNativeDepositsAndOneCompletion(string target, GoalUnit unit, int quantity, bool accepted)
    {
        var catalog = OilCatalogs.Oil() with { MiningSourceTypes = new Dictionary<string, string>
            { ["crude-oil"] = "resource", ["iron-ore"] = "resource", ["tree"] = "tree" } };
        var goal = Goal() with { Category = GoalCategory.Exploration, Target = target, Unit = unit, Quantity = quantity };
        Assert.Equal(accepted, StrategicProductionController.GroundingFailure(goal, "observation", catalog) is null);
    }

    [Theory]
    [InlineData("other", "iron-plate", 10)]
    [InlineData("observation", "iron plates", 10)]
    [InlineData("observation", "iron-plate", 1001)]
    [InlineData("observation", "iron-plate", 0)]
    public void Stale_alias_or_out_of_capability_quantities_are_not_executed(string observation, string item, int quantity)
    {
        Assert.NotNull(StrategicProductionController.GroundingFailure(
            Goal() with { ObservationId = observation, Target = item, Quantity = quantity }, "observation", Catalog()));
    }

    [Fact]
    public void ResearchRequiresAnExactKnownTechnologyAndCompletionUnit()
    {
        var technology = new NativeTechnology("automation", true, false, true, [], [new("red", 1)], 10, 600);
        var technologies = new Dictionary<string, NativeTechnology> { [technology.Name] = technology };
        var goal = Goal() with { Category = GoalCategory.Research, Unit = GoalUnit.Completion, Target = technology.Name, Quantity = 1 };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", Catalog(), technologies));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "invented research" }, "observation", Catalog(), technologies));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Unit = GoalUnit.Items }, "observation", Catalog(), technologies));
    }

    [Fact]
    public void AutomationRatesNeedAnEnabledAssemblerOrFurnaceBandRecipeAndTheFactory()
    {
        var catalog = Catalogs.Early();
        var goal = Goal() with { Target = "automation-science-pack", Unit = GoalUnit.ItemsPerMinute, Quantity = 6, ObservationId = "observation" };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog, automation: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, automation: false));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", catalog, automation: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 601 }, "observation", catalog, automation: true));
        // Steel is smelted from plates in furnace bands; ore smelting stays with resource cells on the patch.
        Assert.Null(StrategicProductionController.GroundingFailure(goal with { Target = "steel-plate" }, "observation", Catalogs.Raw(), automation: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", Catalogs.Raw(), automation: true));
    }

    [Theory]
    [InlineData("coal", true)]
    [InlineData("stone", true)]
    [InlineData("iron-ore", true)]
    [InlineData("iron-plate", true)]
    [InlineData("wood", false)]
    [InlineData("iron plates", false)]
    public void RawRateGoalsUseNativeResourceCellsAndRequireTheFactory(string target, bool accepted)
    {
        // Normal seed20261072: the planner could request a slow coal stock batch, but could not request
        // the persistent electric coal capacity already supported by the resource-cell constructor.
        var catalog = Catalogs.Raw();
        var goal = Goal() with { Target = target, Unit = GoalUnit.ItemsPerMinute, Quantity = 30 };
        Assert.Equal(accepted, StrategicProductionController.GroundingFailure(goal, "observation", catalog,
            automation: true, factory: true) is null);
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, automation: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog, factory: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 0 }, "observation", catalog,
            automation: true, factory: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 601 }, "observation", catalog,
            automation: true, factory: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { ObservationId = "stale" }, "observation", catalog,
            automation: true, factory: true));
        if (accepted)
        {
            var missingItem = catalog with { Items = catalog.Items.Where(p => p.Key != target).ToDictionary() };
            Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", missingItem,
                automation: true, factory: true));
        }
    }

    private static readonly IReadOnlyDictionary<string, long> Turrets = new Dictionary<string, long> { ["gun-turret"] = 32 };
    private static readonly IReadOnlyDictionary<string, long> WallsAndTurrets = new Dictionary<string, long> { ["gun-turret"] = 32, ["stone-wall"] = 40 };

    [Theory]
    [InlineData("plastic-bar", true)]
    [InlineData("sulfur", true)]
    [InlineData("petroleum-gas", false)]
    [InlineData("sulfuric-acid", false)]
    public void AutomationRatesAcceptSolidProductsOfFluidChains(string target, bool accepted)
    {
        // Fluid products are stocks in fluid units, not items per minute; plastic and sulfur leave their chains as items.
        var goal = Goal() with { Target = target, Unit = GoalUnit.ItemsPerMinute, Quantity = 30, ObservationId = "observation" };
        Assert.Equal(accepted, StrategicProductionController.GroundingFailure(goal, "observation", OilCatalogs.Oil(), automation: true) is null);
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", OilCatalogs.Oil(), automation: false));
    }

    [Theory]
    [InlineData("processing-unit", true)]
    [InlineData("battery", true)]
    [InlineData("concrete", true)]
    public void AutomationRatesAcceptPrimedFluidConsumersAndRejectLockedMachines(string target, bool accepted)
    {
        // Acid suppliers are primed before consumer construction; concrete draws terrain water.
        var catalog = OilCatalogs.Advanced();
        var goal = Goal() with { Target = target, Unit = GoalUnit.ItemsPerMinute, Quantity = 10, ObservationId = "observation" };
        Assert.Equal(accepted, StrategicProductionController.GroundingFailure(goal, "observation", catalog, automation: true) is null);
        // Without researched assembling-machine-2, only the locked assembling-machine-3 could make concrete.
        var locked = catalog with { Recipes = catalog.Recipes.Where(r => r.Name != "assembling-machine-2").ToArray() };
        if (target != "battery")
            Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", locked, automation: true));
        else
            Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", locked, automation: true));
    }

    private static GoalProposal Goal() => new("observation", "Accumulate iron plates", GoalCategory.Production,
        "iron-plate", 20, GoalUnit.Items, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null));
    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 2), 100, [],
        new Dictionary<string, NativeItem> { ["iron-plate"] = new(0, 100) },
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
}
