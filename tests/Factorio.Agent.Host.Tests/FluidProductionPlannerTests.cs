using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;
namespace Factorio.Agent.Host.Tests;

public sealed class FluidProductionPlannerTests
{
    [Theory]
    [InlineData("gas", "10.5", true)]
    [InlineData("gas", "0", false)]
    [InlineData("gas", "100001", false)]
    [InlineData("invented-gas", "10", false)]
    [InlineData("refinery", "10", false)]
    public void StrategicFluidGoalsRequireAnExactFluidIdentifierAndBoundedPositiveQuantity(string target, string quantity, bool accepted)
    {
        var goal = new GoalProposal("observation", "Produce fluid", GoalCategory.Production, target,
            decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), GoalUnit.FluidUnits, GoalPriority.Normal,
            new(TimeSpan.Zero, 1, null, null, null));
        Assert.Equal(accepted, StrategicProductionController.GroundingFailure(goal, "observation", Catalog()) is null);
    }

    [Theory]
    [InlineData(null, "installed")]
    [InlineData("refining", "installed")]
    [InlineData("other-recipe", null)]
    public void ReusesUnconfiguredOrMatchingMachineWithoutOverwritingAnotherRecipe(string? recipe, string? expected)
    {
        var plan = new FluidProductionPlanner().Choose("gas", Catalog(), [new("installed", "refinery", recipe)]);
        Assert.Equal(expected, plan!.ExistingId);
    }

    [Fact]
    public void SelectsEnabledNativeFluidConversionInACompatibleMachine()
    {
        var plan = new FluidProductionPlanner().Choose("gas", Catalog(), []);
        Assert.NotNull(plan);
        Assert.Equal("refining", plan.Recipe.Name);
        Assert.Equal("refinery", plan.MachineItem);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoesNotSilentlyExecuteMixedOrUncertainMaterialRecipes(bool random)
    {
        var catalog = Catalog();
        var recipe = catalog.Recipes[0];
        recipe = random ? recipe with { Products = [new("gas", "fluid", 45, Probability: .5)] } :
            recipe with { Ingredients = [new("oil", "fluid", 100), new("coal", "item", 1)] };
        Assert.Null(new FluidProductionPlanner().Choose("gas", catalog with { Recipes = [recipe] }, []));
    }
    [Fact]
    public void ConnectedFluidSegmentIsCountedOnceAcrossItsSourceBoxes()
    {
        var snapshot = new FactorySnapshot("s", new("world", "session", "actor", 1, 1), 10, 100, Protocol.ToElement(new { }),
            [new("segment","fluid","pipe","fluid",Protocol.ToElement(new { aggregateSafe=true,contents=new Dictionary<string,double>{{"oil",123.5}},
                sourceBoxes=new[]{new{entityId="refinery",index=1},new{entityId="refinery",index=2},new{entityId="pipe",index=1}} }))]);
        Assert.Equal(123.5, snapshot.FluidStockAt("refinery", "oil"));
        Assert.Equal(0, snapshot.FluidStockAt("missing", "oil"));
    }
    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 1), 10,
        [new("refining", true, "oil", 5, [new("oil", "fluid", 100)], [new("gas", "fluid", 45)], true)],
        new Dictionary<string, NativeItem> { { "refinery", new(0, 10, PlaceEntity: "refinery", PlaceEntityType: "assembling-machine") } },
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>(),
        new Dictionary<string, NativeAssembler> { { "refinery", new("refinery", new Dictionary<string, bool> { { "oil", true } }, 1, 7000, 255,
            FluidInputCount: 2, FluidOutputCount: 3) } });
}
