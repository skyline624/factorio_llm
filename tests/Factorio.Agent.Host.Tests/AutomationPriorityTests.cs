using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AutomationPriorityTests
{
    [Fact]
    public void AmmunitionDoesNotWaitForAnUnrelatedRegisteredChemicalChain()
    {
        var catalog = AmmunitionCatalog();
        var targets = new Dictionary<string, double> { ["advanced-circuit"] = 30, ["firearm-magazine"] = 10 };
        var old = Plan(catalog, targets);
        var oldOrder = old.Stages.Select(s => s.Recipe).ToList();
        Assert.True(oldOrder.IndexOf("basic-oil-processing") < oldOrder.IndexOf("firearm-magazine"));
        var requested = Plan(catalog, targets, "firearm-magazine");
        Assert.Equal("firearm-magazine", requested.Stages[0].Recipe);
        Assert.Equal(old.Stages.Where(s => s.Recipe != "firearm-magazine"), requested.Stages.Skip(1));
        SameDemand(old, requested);
    }

    [Fact]
    public void TheRequestedConsumerStillVisitsEverySharedSupplierBeforeIt()
    {
        var catalog = Catalogs.Early();
        var targets = new Dictionary<string, double> { ["automation-science-pack"] = 30, ["inserter"] = 12 };
        var requested = Plan(catalog, targets, "inserter");
        var order = requested.Stages.Select(s => s.Recipe).ToList();
        Assert.True(order.IndexOf("copper-cable") < order.IndexOf("electronic-circuit"));
        Assert.True(order.IndexOf("electronic-circuit") < order.IndexOf("inserter"));
        Assert.True(order.IndexOf("iron-gear-wheel") < order.IndexOf("inserter"));
        Assert.True(order.IndexOf("inserter") < order.IndexOf("automation-science-pack"));
        Assert.Equal(42, requested.Stages.Single(s => s.Recipe == "iron-gear-wheel").CraftsPerMinute);
        SameDemand(Plan(catalog, targets), requested);
    }

    [Fact]
    public void PriorityUsesTheCompleteGraphsRefineryAndPreservesItsCoProductCredits()
    {
        var catalog = OilCatalogs.Oil();
        var targets = new Dictionary<string, double> { ["heavy-oil"] = 25, ["plastic-bar"] = 20, ["sulfur"] = 20 };
        var old = Plan(catalog, targets);
        var requested = Plan(catalog, targets, "sulfur");
        Assert.Equal(["advanced-oil-processing", "sulfur", "plastic-bar"], requested.Stages.Select(s => s.Recipe));
        Assert.DoesNotContain(requested.Stages, s => s.Recipe == "basic-oil-processing");
        Assert.Equal("heavy-oil", requested.Stages[0].Item);
        SameDemand(old, requested);
    }

    [Fact]
    public void ARequestedRawItemDoesNotInventAStageOrDropTheOtherTargets()
    {
        var catalog = Catalogs.Raw();
        var targets = new Dictionary<string, double> { ["automation-science-pack"] = 12, ["coal"] = 60 };
        var old = Plan(catalog, targets);
        var requested = Plan(catalog, targets, "coal");
        Assert.Equal(old.Stages, requested.Stages);
        Assert.Equal(60, requested.RawPerMinute["coal"]);
        SameDemand(old, requested);
    }

    [Fact]
    public void PriorityKeepsTheFullRateAndTheExistingPerCallConstructionBudget()
    {
        var catalog = AmmunitionCatalog();
        var targets = new Dictionary<string, double> { ["advanced-circuit"] = 30, ["firearm-magazine"] = 600 };
        var old = Plan(catalog, targets);
        var requested = Plan(catalog, targets, "firearm-magazine");
        var first = requested.Stages[0];
        Assert.Equal(600, first.CraftsPerMinute);
        Assert.True(first.Machines > AutomationPlanner.MaximumNewMachinesPerStage);
        Assert.Equal(AutomationPlanner.MaximumNewMachinesPerStage, AutomationPlanner.MissingMachines(catalog, first, []));
        SameDemand(old, requested);
    }

    [Fact]
    public void AnUnregisteredPriorityCannotIntroduceAnUnrequestedGoal()
    {
        Assert.Throws<ArgumentException>(() => Plan(Catalogs.Early(),
            new Dictionary<string, double> { ["automation-science-pack"] = 12 }, "inserter"));
    }

    private static AutomationPlan Plan(ProductionCatalog catalog, IReadOnlyDictionary<string, double> targets, string? priority = null) =>
        AutomationPlanner.Plan(catalog, targets, FactoryDirector.MachineItems(catalog), FluidChainDirector.Machines(catalog), priority);

    private static void SameDemand(AutomationPlan before, AutomationPlan after)
    {
        Assert.Equal(before.Stages.OrderBy(s => s.Recipe), after.Stages.OrderBy(s => s.Recipe));
        Assert.Equal(before.RawPerMinute.OrderBy(p => p.Key), after.RawPerMinute.OrderBy(p => p.Key));
        Assert.Equal(before.FluidSources, after.FluidSources);
    }

    private static ProductionCatalog AmmunitionCatalog()
    {
        var oil = OilCatalogs.Oil();
        return oil with { Items = new Dictionary<string, NativeItem>(oil.Items)
            { ["firearm-magazine"] = new(0, 200, MagazineSize: 10), ["advanced-circuit"] = new(0, 200) },
            Recipes = [.. oil.Recipes,
                new("firearm-magazine", true, "crafting", 1, [new("iron-plate", "item", 4)], [new("firearm-magazine", "item", 1)], false),
                new("advanced-circuit", true, "crafting", 6, [new("electronic-circuit", "item", 2), new("plastic-bar", "item", 2),
                    new("copper-cable", "item", 4)], [new("advanced-circuit", "item", 1)], false)] };
    }
}
