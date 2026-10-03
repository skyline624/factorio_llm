using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AutomationDemandTests
{
    private static readonly HashSet<string> Machines = new(StringComparer.Ordinal) { "assembling-machine-1" };

    [Fact]
    public void TargetsShareTheirIntermediateStages()
    {
        // Campaign 2026-09-30 (seed 20261002): the green science plan counted the gear cell built for red science as its own,
        // so one gear cell fed both packs and the inserter cell starved for two hours.
        var plan = AutomationPlanner.Plan(Catalogs.Early(),
            new Dictionary<string, double> { ["automation-science-pack"] = 12, ["inserter"] = 12 }, Machines);
        var gears = plan.Stages.Single(s => s.Recipe == "iron-gear-wheel");
        Assert.Equal(24, gears.CraftsPerMinute, 6);
        Assert.Equal(72, plan.RawPerMinute["iron-plate"], 6);
        Assert.Equal(30, plan.RawPerMinute["copper-plate"], 6);
        Assert.Equal(12, AutomationPlanner.Plan(Catalogs.Early(), "automation-science-pack", 12, Machines)
            .Stages.Single(s => s.Recipe == "iron-gear-wheel").CraftsPerMinute, 6);
    }

    [Fact]
    public void MissingCellsComeFromTheNativeCapacityOfReadyCellsNotTheirCount()
    {
        var catalog = WithFastAssembler(Catalogs.Early());
        // A gear cell is held to one arm: 0.8 plates/s gives 24 gears a minute, whatever the machine.
        Assert.Equal(24, AutomationPlanner.CellCraftsPerMinute(catalog, Recipe(catalog, "iron-gear-wheel"), "assembling-machine-1"), 6);
        var gears = new AutomationStage("iron-gear-wheel", "iron-gear-wheel", "assembling-machine-1", 24, 1);
        Assert.Equal(0, AutomationPlanner.MissingMachines(catalog, gears, ["assembling-machine-1"]));
        Assert.Equal(1, AutomationPlanner.MissingMachines(catalog, gears with { CraftsPerMinute = 30 }, ["assembling-machine-1"]));
        // Science: two slow cells give 12 packs a minute; a plan of 18 sized for faster machines still needs one more cell.
        var science = new AutomationStage("automation-science-pack", "automation-science-pack", "assembling-machine-2", 18, 2);
        Assert.Equal(1, AutomationPlanner.MissingMachines(catalog, science, ["assembling-machine-1", "assembling-machine-1"]));
        Assert.Equal(0, AutomationPlanner.MissingMachines(catalog, science, ["assembling-machine-2", "assembling-machine-2"]));
        // The per-stage cell budget still bounds what is added.
        Assert.Equal(2, AutomationPlanner.MissingMachines(catalog, science with { CraftsPerMinute = 600 }, ["assembling-machine-1"],
            maximumNewMachinesPerStage: 2));
    }

    [Theory]
    [InlineData("copper-cable", 498, 249, 11, 3)]
    [InlineData("electronic-circuit", 106, 106, 9, 1)]
    public void EightExistingCellsDoNotPreventTheRestOfTheRequestedStage(string item, double rate, double crafts, int total, int missing)
    {
        // Normal seed20261019: eight cable cells cover192crafts/min instead of249; eight circuit cells cover96 instead of106.
        var catalog = WithFastAssembler(Catalogs.Early());
        var machines = new HashSet<string>(StringComparer.Ordinal) { "assembling-machine-2" };
        var stage = AutomationPlanner.Plan(catalog, item, rate, machines).Stages.Single(s => s.Item == item);
        var recipe = Recipe(catalog, stage.Recipe);
        double capacity = AutomationPlanner.CellCraftsPerMinute(catalog, recipe, stage.MachineItem);
        Assert.Equal(crafts, stage.CraftsPerMinute, 6);
        Assert.Equal(total, stage.Machines);
        Assert.True(8 * capacity < crafts);
        Assert.Equal(missing, AutomationPlanner.MissingMachines(catalog, stage, Enumerable.Repeat(stage.MachineItem, 8).ToArray()));
        Assert.True(total * capacity >= crafts);
        Assert.Equal(0, AutomationPlanner.MissingMachines(catalog, stage, Enumerable.Repeat(stage.MachineItem, total).ToArray()));
    }

    [Fact]
    public void RepeatedBoundedCallsFinishALargeStageWithoutLosingItsDemand()
    {
        var catalog = Catalogs.Early();
        var stage = AutomationPlanner.Plan(catalog, "copper-cable", 1000, Machines).Stages.Single();
        Assert.Equal(21, stage.Machines);
        var ready = new List<string>();
        foreach (int expected in new[] { 8, 8, 5, 0 })
        {
            int missing = AutomationPlanner.MissingMachines(catalog, stage, ready);
            Assert.Equal(expected, missing);
            ready.AddRange(Enumerable.Repeat(stage.MachineItem, missing));
        }
        Assert.Equal(stage.Machines, ready.Count);
        Assert.True(ready.Sum(m => AutomationPlanner.CellCraftsPerMinute(catalog, Recipe(catalog, stage.Recipe), m)) >= stage.CraftsPerMinute);
    }

    [Fact]
    public void RegisteredTargetsKeepTheHighestRequestedRate()
    {
        var state = new FactoryState(1, "world", [], []).WithTarget("automation-science-pack", 12).WithTarget("automation-science-pack", 6)
            .WithTarget("logistic-science-pack", 3);
        Assert.Equal(12, state.Targets!["automation-science-pack"]);
        Assert.Equal(3, state.Targets["logistic-science-pack"]);
    }

    private static NativeRecipe Recipe(ProductionCatalog catalog, string name) => catalog.Recipes.Single(r => r.Name == name);

    private static ProductionCatalog WithFastAssembler(ProductionCatalog catalog) => catalog with
    {
        Assemblers = new Dictionary<string, NativeAssembler>(catalog.Assemblers!)
        {
            ["assembling-machine-2"] = new("assembling-machine-2", new Dictionary<string, bool> { ["crafting"] = true, ["basic-crafting"] = true }, 0.75, 2500, 255)
        }
    };
}
