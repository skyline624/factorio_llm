using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicGroundingTests
{
    [Fact]
    public void DefenseMeansInstalledNativeTurretsRatherThanCarriedItemStock()
    {
        var catalog = Catalog() with { Items = new Dictionary<string, NativeItem>(Catalog().Items)
            { ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret") } };
        var goal = Goal() with { Category = GoalCategory.Defense, Target = "gun-turret", Quantity = 8 };
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", catalog));
        catalog = catalog with { Turrets = new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) } };
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Target = "iron-plate" }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 0 }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 33 }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Quantity = 1.5m }, "observation", catalog));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal with { Unit = GoalUnit.Completion }, "observation", catalog));
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

    private static GoalProposal Goal() => new("observation", "Accumulate iron plates", GoalCategory.Production,
        "iron-plate", 20, GoalUnit.Items, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null));
    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 2), 100, [],
        new Dictionary<string, NativeItem> { ["iron-plate"] = new(0, 100) },
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
}
