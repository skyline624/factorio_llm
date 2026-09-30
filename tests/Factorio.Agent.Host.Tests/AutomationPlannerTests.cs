using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AutomationPlannerTests
{
    private static readonly HashSet<string> Machines = new(StringComparer.Ordinal) { "assembling-machine-1" };

    [Fact]
    public void RedScienceChainsGearsAndLeavesPlatesToSmelting()
    {
        var plan = AutomationPlanner.Plan(Catalogs.Early(), "automation-science-pack", 6, Machines);
        var science = plan.Stages.Single(s => s.Recipe == "automation-science-pack");
        var gears = plan.Stages.Single(s => s.Recipe == "iron-gear-wheel");
        Assert.Equal(6, science.CraftsPerMinute, 6);
        Assert.Equal(1, science.Machines);
        Assert.Equal(6, gears.CraftsPerMinute, 6);
        Assert.Equal(1, gears.Machines);
        Assert.Equal(12, plan.RawPerMinute["iron-plate"], 6);
        Assert.Equal(6, plan.RawPerMinute["copper-plate"], 6);
    }

    [Fact]
    public void InserterThroughputBoundsCellsBeforeCraftingSpeed()
    {
        // 60 gears per minute needs 120 plates per minute: three cells at 0.8 plates per second each.
        var gears = AutomationPlanner.Plan(Catalogs.Early(), "iron-gear-wheel", 60, Machines).Stages.Single();
        Assert.Equal(3, gears.Machines);
    }

    [Fact]
    public void SharedIntermediatesAreSummedAndMachinesCapped()
    {
        var plan = AutomationPlanner.Plan(Catalogs.Early(), "inserter", 600, Machines, maximumMachinesPerStage: 4);
        var gears = plan.Stages.Single(s => s.Recipe == "iron-gear-wheel");
        Assert.Equal(600, gears.CraftsPerMinute, 6);
        Assert.All(plan.Stages, s => Assert.InRange(s.Machines, 1, 4));
        Assert.True(plan.RawPerMinute.ContainsKey("copper-plate"));
    }

    [Fact]
    public void ItemsWithoutAnAssemblerRecipeAreRaw()
    {
        var plan = AutomationPlanner.Plan(Catalogs.Early(), "iron-plate", 30, Machines);
        Assert.Empty(plan.Stages);
        Assert.Equal(30, plan.RawPerMinute["iron-plate"]);
    }
}

internal static class Catalogs
{
    private static NativeMaterial Item(string name, double amount) => new(name, "item", amount);
    private static NativeRecipe Recipe(string name, double seconds, string category, NativeMaterial[] ingredients, params NativeMaterial[] products) =>
        new(name, true, category, seconds, ingredients, products, false);

    public static ProductionCatalog Early() => new(new("world", "session", "actor", 1, 1), 1,
        [
            Recipe("iron-plate", 3.2, "smelting", [Item("iron-ore", 1)], Item("iron-plate", 1)),
            Recipe("copper-plate", 3.2, "smelting", [Item("copper-ore", 1)], Item("copper-plate", 1)),
            Recipe("iron-gear-wheel", 0.5, "crafting", [Item("iron-plate", 2)], Item("iron-gear-wheel", 1)),
            Recipe("copper-cable", 0.5, "crafting", [Item("copper-plate", 1)], Item("copper-cable", 2)),
            Recipe("electronic-circuit", 0.5, "crafting", [Item("iron-plate", 1), Item("copper-cable", 3)], Item("electronic-circuit", 1)),
            Recipe("inserter", 0.5, "crafting", [Item("electronic-circuit", 1), Item("iron-gear-wheel", 1), Item("iron-plate", 1)], Item("inserter", 1)),
            Recipe("automation-science-pack", 5, "crafting", [Item("copper-plate", 1), Item("iron-gear-wheel", 1)], Item("automation-science-pack", 1)),
            Recipe("assembling-machine-1", 0.5, "crafting", [Item("electronic-circuit", 3), Item("iron-gear-wheel", 5), Item("iron-plate", 9)], Item("assembling-machine-1", 1))
        ],
        new Dictionary<string, NativeItem>
        {
            ["iron-plate"] = new(0, 100), ["copper-plate"] = new(0, 100), ["iron-gear-wheel"] = new(0, 100),
            ["copper-cable"] = new(0, 200), ["electronic-circuit"] = new(0, 200), ["inserter"] = new(0, 50),
            ["automation-science-pack"] = new(0, 200), ["assembling-machine-1"] = new(0, 50, PlaceEntity: "assembling-machine-1", PlaceEntityType: "assembling-machine")
        },
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(),
        new Dictionary<string, bool> { ["crafting"] = true },
        new Dictionary<string, NativeAssembler>
        {
            ["assembling-machine-1"] = new("assembling-machine-1", new Dictionary<string, bool> { ["crafting"] = true, ["basic-crafting"] = true }, 0.5, 2500, 255)
        });

    /// <summary>Early catalog plus base-game deposits, smelting and extraction equipment.</summary>
    public static ProductionCatalog Raw(bool electricDrill = true)
    {
        var early = Early();
        NativeItem Placed(string name, string type, int stack = 50) => new(0, stack, PlaceEntity: name, PlaceEntityType: type);
        var items = new Dictionary<string, NativeItem>(early.Items)
        {
            ["coal"] = new(4000000, 50, FuelCategory: "chemical"), ["iron-ore"] = new(0, 50), ["copper-ore"] = new(0, 50),
            ["stone"] = new(0, 50), ["stone-brick"] = new(0, 100), ["steel-plate"] = new(0, 100),
            ["stone-furnace"] = Placed("stone-furnace", "furnace"), ["iron-chest"] = Placed("iron-chest", "container"),
            ["wooden-chest"] = Placed("wooden-chest", "container"), ["small-electric-pole"] = Placed("small-electric-pole", "electric-pole"),
            ["electric-mining-drill"] = Placed("electric-mining-drill", "mining-drill"),
            ["burner-mining-drill"] = Placed("burner-mining-drill", "mining-drill"),
            ["inserter"] = Placed("inserter", "inserter")
        };
        return early with
        {
            Scope = FactoryMaps.Grass(1).Scope,
            Recipes = [.. early.Recipes,
                Recipe("stone-brick", 3.2, "smelting", [Item("stone", 2)], Item("stone-brick", 1)),
                Recipe("steel-plate", 16, "smelting", [Item("iron-plate", 5)], Item("steel-plate", 1)),
                Recipe("stone-furnace", 0.5, "crafting", [Item("stone", 5)], Item("stone-furnace", 1)),
                Recipe("iron-chest", 0.5, "crafting", [Item("iron-plate", 8)], Item("iron-chest", 1)),
                Recipe("small-electric-pole", 0.5, "crafting", [Item("copper-cable", 2)], Item("small-electric-pole", 2)),
                Recipe("burner-mining-drill", 2, "crafting", [Item("iron-gear-wheel", 3), Item("stone-furnace", 1), Item("iron-plate", 3)], Item("burner-mining-drill", 1)),
                new("electric-mining-drill", electricDrill, "crafting", 2, [Item("electronic-circuit", 3), Item("iron-gear-wheel", 5), Item("iron-plate", 10)],
                    [Item("electric-mining-drill", 1)], false)],
            Items = items,
            Mining = new Dictionary<string, NativeMaterial[]>
            {
                ["iron-ore"] = [Item("iron-ore", 1)], ["copper-ore"] = [Item("copper-ore", 1)], ["coal"] = [Item("coal", 1)],
                ["stone"] = [Item("stone", 1)], ["tree"] = [Item("wood", 4)]
            },
            MiningSourceTypes = new Dictionary<string, string>
            {
                ["iron-ore"] = "resource", ["copper-ore"] = "resource", ["coal"] = "resource", ["stone"] = "resource", ["tree"] = "tree"
            },
            Machines = new Dictionary<string, NativeFurnace>
            {
                ["stone-furnace"] = new("stone-furnace", new Dictionary<string, bool> { ["smelting"] = true },
                    new Dictionary<string, bool> { ["chemical"] = true }, 1)
            }
        };
    }
}
