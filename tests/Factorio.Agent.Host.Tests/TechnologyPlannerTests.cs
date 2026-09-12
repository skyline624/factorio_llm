using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TechnologyPlannerTests
{
    [Fact]
    public void NativeScienceIngredientDoesNotRequireARecipeMaterialType()
    {
        var value = Protocol.ToElement(new { name = "automation", enabled = true, researched = false, available = true,
            prerequisites = Array.Empty<string>(), ingredients = new[] { new { name = "science-pack", amount = 1 } }, count = 10, energyTicks = 600 });
        var technology = System.Text.Json.JsonSerializer.Deserialize<NativeTechnology>(value, Protocol.Json)!;
        Assert.Equal(new TechnologyStep("research", "automation"), new TechnologyPlanner().Next("automation", Map(technology)));
    }

    [Fact]
    public void ResearchFirstRequiresTheNativeCraftTriggerOfItsPrerequisite()
    {
        var technologies = Map(Technology("science") with
            { Trigger = Protocol.ToElement(new { type = "craft-item", item = new { name = "laboratory" }, count = 1 }) },
            Technology("automation") with { Available = false, Prerequisites = ["science"] });
        Assert.Equal(new TechnologyStep("craft-trigger", "science", "laboratory", 1), new TechnologyPlanner().Next("automation", technologies));
    }

    [Fact]
    public void UnknownTriggerCannotBeReplacedByAnOrdinaryResearchSelection()
    {
        var technology = Technology("unknown") with { Trigger = Protocol.ToElement(new { type = "capture-spawner" }) };
        Assert.Equal("unsupported", new TechnologyPlanner().Next("unknown", Map(technology)).Kind);
    }

    [Fact]
    public void CyclicPrerequisitesAreRejected()
    {
        var technologies = Map(Technology("a") with { Prerequisites = ["b"] }, Technology("b") with { Prerequisites = ["a"] });
        Assert.Throws<InvalidDataException>(() => new TechnologyPlanner().Next("a", technologies));
    }

    [Fact]
    public void CompletedResearchDoesNotRepeatItsTrigger()
    {
        var technology = Technology("done") with { Researched = true, Enabled = false };
        Assert.Equal("completed", new TechnologyPlanner().Next("done", Map(technology)).Kind);
    }

    [Fact]
    public void UnlockedScienceResearchIsSelectedAfterItsPrerequisitesComplete()
    {
        var technology = Technology("automation") with { Ingredients = [new("science-pack", 1)], Count = 10 };
        Assert.Equal(new TechnologyStep("research", "automation"), new TechnologyPlanner().Next("automation", Map(technology)));
    }

    [Fact]
    public void NativeMineTriggerRetainsItsExactResourceIdentity()
    {
        var technology = Technology("oil") with { Trigger = Protocol.ToElement(new { type = "mine-entity", entity = "crude-oil" }) };
        var step = new TechnologyPlanner().Next("oil", Map(technology));
        Assert.Equal("mine-trigger", step.Kind);
        Assert.Equal("crude-oil", step.Entity);
    }

    private static NativeTechnology Technology(string name) => new(name, true, false, true, [], [], 1, 600);
    private static IReadOnlyDictionary<string, NativeTechnology> Map(params NativeTechnology[] values) => values.ToDictionary(v => v.Name);
}
