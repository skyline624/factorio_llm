using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryCompositionTests
{
    [Fact]
    public void ProcessorAndCircuitTargetsShareTheirSolidAndChemicalSuppliers()
    {
        var catalog = Catalog();
        var plan = Plan(catalog, new Dictionary<string, double> { ["processing-unit"] = 1, ["advanced-circuit"] = 2 });
        double Crafts(string recipe) => plan.Stages.Single(s => s.Recipe == recipe).CraftsPerMinute;
        Assert.Equal(4, Crafts("advanced-circuit"), 6);
        Assert.Equal(28, Crafts("electronic-circuit"), 6);
        Assert.Equal(50, Crafts("copper-cable"), 6);
        Assert.Equal(4, Crafts("plastic-bar"), 6);
        Assert.Equal(0.1, Crafts("sulfuric-acid"), 6);
        Assert.Equal(28.1, plan.RawPerMinute["iron-plate"], 6);
        Assert.Equal(50, plan.RawPerMinute["copper-plate"], 6);
        Assert.Equal(4, plan.RawPerMinute["coal"], 6);
        Assert.Equal(17.5, plan.FluidSources.Single(s => s.Fluid == "water").UnitsPerMinute, 6);
        Assert.Equal(87.5 / 45 * 100, plan.FluidSources.Single(s => s.Fluid == "crude-oil").UnitsPerMinute, 6);
        Assert.DoesNotContain("plastic-bar", plan.RawPerMinute.Keys);
        Assert.DoesNotContain("sulfuric-acid", plan.RawPerMinute.Keys);
        Assert.DoesNotContain("water", plan.RawPerMinute.Keys);
        var order = plan.Stages.Select(s => s.Recipe).ToList();
        Assert.True(order.IndexOf("basic-oil-processing") < order.IndexOf("plastic-bar"));
        Assert.True(order.IndexOf("plastic-bar") < order.IndexOf("advanced-circuit"));
        Assert.True(order.IndexOf("copper-cable") < order.IndexOf("electronic-circuit"));
        Assert.True(order.IndexOf("electronic-circuit") < order.IndexOf("advanced-circuit"));
        Assert.True(order.IndexOf("sulfuric-acid") < order.IndexOf("processing-unit"));
        Assert.Equal(AutomationPlanner.FluidKind, plan.Stages.Single(s => s.Recipe == "processing-unit").Kind);
        Assert.Equal("assembler", plan.Stages.Single(s => s.Recipe == "advanced-circuit").Kind);
    }

    [Fact]
    public void FluidVolumeDoesNotCountAgainstAnItemInserter()
    {
        var catalog = Catalog();
        var recipe = catalog.Recipes.Single(r => r.Name == "basic-oil-processing");
        Assert.Equal(12, AutomationPlanner.CellCraftsPerMinute(catalog, recipe, "oil-refinery"), 6);
        var plan = Plan(catalog, new Dictionary<string, double> { ["plastic-bar"] = 60, ["sulfur"] = 60 });
        var stage = plan.Stages.Single(s => s.Recipe == "basic-oil-processing");
        Assert.Equal(60 * 20 / 2.0 / 45 + 60 * 30 / 2.0 / 45, stage.CraftsPerMinute, 6);
        Assert.Equal(3, stage.Machines);
    }

    [Fact]
    public void UnresearchedChemicalOutputsStayExplicitUnsupportedRawItems()
    {
        var catalog = Catalog();
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "plastic-bar" ? r with { Enabled = false } : r).ToArray() };
        var plan = Plan(catalog, new Dictionary<string, double> { ["advanced-circuit"] = 2 });
        Assert.Equal(4, plan.RawPerMinute["plastic-bar"]);
        Assert.DoesNotContain(plan.Stages, s => s.Recipe == "plastic-bar");
    }

    [Fact]
    public void ChemicalCellsUseSharedDemandForBuffersAndStockCaps()
    {
        var catalog = Catalog();
        var cells = new[] { new FactoryCell("plastic", 0, new(0, 0, true), "fluid", "chemical-plant", "plastic-bar",
            new Dictionary<string, string>(), "ready", 1) };
        var state = new FactoryState(1, catalog.Scope.WorldId, [], cells, Targets: new Dictionary<string, double> { ["advanced-circuit"] = 2 });
        var shares = FactoryLogistics.CellShares(catalog, state)!;
        Assert.Equal(2, shares["plastic-bar"], 6);
        Assert.Equal(20, FactoryLogistics.CellBufferCrafts(cells[0], shares, 40));
        Assert.Equal(80, FactoryLogistics.StockCaps(catalog, state)!["plastic-bar"]);
        Assert.DoesNotContain("petroleum-gas", FactoryLogistics.StockCaps(catalog, state)!.Keys);
    }

    [Theory]
    [InlineData("battery", false)]
    [InlineData("processing-unit", false)]
    [InlineData("processing-unit", true)]
    public async Task PreparedConsumerVariantsCannotTouchANormalCampaign(string item, bool fromMaterials)
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FluidConsumerQualification(session, item, fromMaterials).RunAsync(CancellationToken.None));
        Assert.Contains("fixture", error.Message);
    }

    private static AutomationPlan Plan(ProductionCatalog catalog, IReadOnlyDictionary<string, double> targets) =>
        AutomationPlanner.Plan(catalog, targets, FactoryDirector.MachineItems(catalog), fluidMachineItems: FluidChainDirector.Machines(catalog));

    private static ProductionCatalog Catalog()
    {
        var catalog = OilCatalogs.Advanced();
        return catalog with { Recipes = [.. catalog.Recipes, new("advanced-circuit", true, "crafting", 6,
            [new("electronic-circuit", "item", 2), new("plastic-bar", "item", 2), new("copper-cable", "item", 4)],
            [new("advanced-circuit", "item", 1)], false)] };
    }
}
