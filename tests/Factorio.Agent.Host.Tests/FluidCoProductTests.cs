using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidCoProductTests
{
    [Theory]
    [InlineData(25, 45, 55, 1)]
    [InlineData(50, 45, 55, 2)]
    [InlineData(25, 90, 55, 2)]
    [InlineData(25, 45, 165, 3)]
    public void SimultaneousOutputsShareTheMaximumRequiredCycles(double heavy, double light, double gas, double crafts)
    {
        var catalog = OilCatalogs.Oil();
        var plan = AutomationPlanner.Plan(catalog, new Dictionary<string, double>
            { ["heavy-oil"] = heavy, ["light-oil"] = light, ["petroleum-gas"] = gas }, FactoryDirector.MachineItems(catalog),
            fluidMachineItems: FluidChainDirector.Machines(catalog));
        var refinery = Assert.Single(plan.Stages);
        Assert.Equal("advanced-oil-processing", refinery.Recipe);
        Assert.Equal(crafts, refinery.CraftsPerMinute, 6);
        Assert.Equal(crafts * 100, plan.FluidSources.Single(s => s.Fluid == "crude-oil").UnitsPerMinute, 6);
        Assert.Equal(crafts * 50, plan.FluidSources.Single(s => s.Fluid == "water").UnitsPerMinute, 6);
        Assert.Empty(plan.RawPerMinute);
    }

    [Fact]
    public void LightOilUsesItsOwnOutputAmountRatherThanTheFirstHeavyOilAmount()
    {
        var plan = OilCatalogs.Plan(OilCatalogs.Oil(), "light-oil", 90)!;
        var refinery = Assert.Single(plan.Stages);
        Assert.Equal("light-oil", refinery.Item);
        Assert.Equal(2, refinery.CraftsPerMinute, 6);
    }

    [Fact]
    public void GasAloneKeepsTheSingleOutputRecipeWithoutUnusedCoProducts()
    {
        Assert.Equal("basic-oil-processing", Assert.Single(OilCatalogs.Plan(OilCatalogs.Oil(), "petroleum-gas", 45)!.Stages).Recipe);
    }

    [Theory]
    [InlineData(11, 110 / 45.0)]
    [InlineData(30, 300 / 55.0)]
    public void ChemicalConsumerGasUsesTheCreditFromTheFuelRefinery(double plastic, double crafts)
    {
        var catalog = RocketCatalog();
        var plan = AutomationPlanner.Plan(catalog, new Dictionary<string, double> { ["plastic-bar"] = plastic, ["rocket-fuel"] = 1 },
            FactoryDirector.MachineItems(catalog), fluidMachineItems: FluidChainDirector.Machines(catalog));
        Assert.DoesNotContain(plan.Stages, s => s.Recipe == "basic-oil-processing");
        Assert.Equal(crafts, plan.Stages.Single(s => s.Recipe == "advanced-oil-processing").CraftsPerMinute, 6);
        Assert.Equal(crafts * 100, plan.FluidSources.Single(s => s.Fluid == "crude-oil").UnitsPerMinute, 6);
        Assert.Equal(plastic / 2, plan.RawPerMinute["coal"], 6);
        var recipes = plan.Stages.Select(s => s.Recipe).ToList();
        Assert.True(recipes.IndexOf("advanced-oil-processing") < recipes.IndexOf("plastic-bar"));
        Assert.True(recipes.IndexOf("solid-fuel-from-light-oil") < recipes.IndexOf("rocket-fuel"));
    }

    [Fact]
    public void RocketFuelReusesLightOilForItsSolidFuelAndItsOwnFluidInput()
    {
        var catalog = RocketCatalog();
        var plan = OilCatalogs.Plan(catalog, "rocket-fuel", 1)!;
        Assert.Equal(["advanced-oil-processing", "solid-fuel-from-light-oil", "rocket-fuel"], plan.Stages.Select(s => s.Recipe));
        Assert.Equal(110 / 45.0, plan.Stages[0].CraftsPerMinute, 6);
        Assert.Equal(10, plan.Stages[1].CraftsPerMinute, 6);
        Assert.Equal(1, plan.Stages[2].CraftsPerMinute, 6);
        Assert.Empty(plan.RawPerMinute);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("mixed")]
    [InlineData("probabilistic")]
    [InlineData("self-input")]
    [InlineData("unavailable-water")]
    public void UnsupportedCoProductsCannotAuthorizeLightOilSupply(string fault)
    {
        var catalog = OilCatalogs.Oil();
        var advanced = catalog.Recipes.Single(r => r.Name == "advanced-oil-processing");
        advanced = fault switch
        {
            "disabled" => advanced with { Enabled = false },
            "mixed" => advanced with { Products = [advanced.Products[0], new("barrel", "item", 1), advanced.Products[2]] },
            "probabilistic" => advanced with { Products = advanced.Products.Select(p => p with { Probability = .5 }).ToArray() },
            "self-input" => advanced with { Ingredients = [.. advanced.Ingredients, OilCatalogs.Fluid("heavy-oil", 5)] },
            _ => advanced
        };
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == advanced.Name ? advanced : r).ToArray(),
            TerrainFluids = fault == "unavailable-water" ? [] : catalog.TerrainFluids };
        Assert.Null(OilCatalogs.Choose(catalog, "light-oil"));
    }

    [Theory]
    [InlineData("rocket-fuel")]
    [InlineData("electric-engine-unit")]
    public async Task PreparedOilProductTestsRejectNormalWorldsBeforeTakingControl(string item)
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OilProductQualification(session, item).RunAsync(CancellationToken.None));
    }

    private static ProductionCatalog RocketCatalog()
    {
        var catalog = OilCatalogs.Advanced();
        return catalog with { Recipes = [.. catalog.Recipes,
            new("solid-fuel-from-heavy-oil", true, "chemistry", 1, [OilCatalogs.Fluid("heavy-oil", 20)], [new("solid-fuel", "item", 1)], true),
            new("solid-fuel-from-light-oil", true, "chemistry", 1, [OilCatalogs.Fluid("light-oil", 10)], [new("solid-fuel", "item", 1)], true),
            new("solid-fuel-from-petroleum-gas", true, "chemistry", 1, [OilCatalogs.Fluid("petroleum-gas", 20)], [new("solid-fuel", "item", 1)], true),
            new("rocket-fuel", true, "crafting-with-fluid", 15, [new("solid-fuel", "item", 10), OilCatalogs.Fluid("light-oil", 10)],
                [new("rocket-fuel", "item", 1)], true)] };
    }
}
