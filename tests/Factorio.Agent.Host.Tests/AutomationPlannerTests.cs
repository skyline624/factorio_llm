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
}
