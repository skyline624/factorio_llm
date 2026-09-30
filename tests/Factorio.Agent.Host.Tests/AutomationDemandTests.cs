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
        Assert.Equal(1, AutomationPlanner.MissingMachines(catalog, science with { CraftsPerMinute = 600 }, ["assembling-machine-1"],
            maximumMachinesPerStage: 2));
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
