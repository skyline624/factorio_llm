using System.Text.Json;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DecisionShadowTests
{
    private const string Facts = """
        {"observedTick":50014,"agent":{"alive":true,"health":250,"inventory":{"lab":1,"iron-plate":14},"ammoRounds":100},
         "visibleEnemyCount":0,
         "availableSolidRecipes":["iron-gear-wheel","automation-science-pack","gun-turret"],
         "availableResearch":[
           {"name":"lamp","count":10,"ingredients":[{"name":"automation-science-pack","amount":1}],"trigger":null},
           {"name":"automation","count":10,"ingredients":[{"name":"automation-science-pack","amount":1}],"trigger":null},
           {"name":"logistic-science-pack","count":75,"ingredients":[{"name":"automation-science-pack","amount":1}],"trigger":null}],
         "researchedTechnologies":["automation-science-pack","electronics","steam-power"],
         "rocketResearchDependencies":{"prerequisites":{"automation-2":["automation","logistic-science-pack"],"rocket-silo":["automation-2"]}},
         "automatedFactory":{"available":true,"assemblerCells":[{"recipe":"iron-gear-wheel","cells":1}],"laboratories":1},
         "knownDefenses":{"turrets":[],"exposedIndustrialAnchors":7},
         "nativeDefenseItems":["gun-turret"]}
        """;

    private static GoalProposal Goal(GoalCategory category, string target, GoalUnit unit) =>
        new("o", "Research automation to unlock assemblers", category, target, 1, unit, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null));

    [Fact]
    public void OptionsAreComputedFromFactsWithSiloPathResearchFirst()
    {
        var question = DecisionShadow.Build(Facts, null, Goal(GoalCategory.Research, "automation", GoalUnit.Completion));
        var options = question.Questions["next_goal"].Criteria!;
        Assert.Equal(["research:automation", "research:logistic-science-pack", "research:lamp"],
            options.Keys.Where(k => k.StartsWith("research:")).ToArray());
        Assert.Contains("rocket silo path", options["research:automation"]);
        Assert.DoesNotContain("rocket silo path", options["research:lamp"]);
        Assert.Contains("automate:automation-science-pack", options.Keys);
        Assert.Contains("defense:gun-turret", options.Keys);
        Assert.Contains("none", options.Keys);
        Assert.InRange(options.Count, 2, 26);
        Assert.Equal("research:automation", question.ProposedOption);
        Assert.Equal("noul", question.Questions["proposal_sound"].Type);
    }

    [Theory]
    [InlineData(GoalCategory.Defense, "gun-turret", GoalUnit.Items, "defense:gun-turret")]
    [InlineData(GoalCategory.Production, "automation-science-pack", GoalUnit.ItemsPerMinute, "automate:automation-science-pack")]
    [InlineData(GoalCategory.Production, "copper-plate", GoalUnit.Items, null)]
    public void TheProposalMapsToItsOptionOnlyWhenOffered(GoalCategory category, string target, GoalUnit unit, string? expected) =>
        Assert.Equal(expected, DecisionShadow.Build(Facts, null, Goal(category, target, unit)).ProposedOption);

    [Fact]
    public void StateStaysCompactForTheEightThousandTokenWindow()
    {
        var question = DecisionShadow.Build(Facts, "{\"goal\":{\"target\":\"automation\"},\"executionFailure\":\"navigation_blocked\"}",
            Goal(GoalCategory.Research, "automation", GoalUnit.Completion));
        Assert.True(question.State.Length < 6000);
        using var state = JsonDocument.Parse(question.State);
        Assert.Equal(7, state.RootElement.GetProperty("exposedIndustrialAnchors").GetInt32());
        Assert.Contains("navigation_blocked", question.State);
    }

    [Fact]
    public async Task ContextsWithoutAlternativesAreRecordedWithoutCallingTheModel()
    {
        var question = DecisionShadow.Build("{\"observedTick\":5,\"death\":{\"tick\":4}}", null, Goal(GoalCategory.Recovery, "corpse", GoalUnit.Completion));
        using var http = new HttpClient(new ThrowingHandler());
        var verdict = await DecisionShadow.AskAsync(new DecisionModelClient(http, new DecisionModelOptions()), question, default);
        Assert.NotNull(verdict.Error);
        Assert.Null(verdict.ModelChoice);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The model must not be called.");
    }

    [Fact]
    public void AtMostTwentySixOptionsAreOffered()
    {
        var research = string.Join(",", Enumerable.Range(0, 40).Select(i =>
            $"{{\"name\":\"tech-{i}\",\"count\":10,\"ingredients\":[{{\"name\":\"automation-science-pack\",\"amount\":1}}],\"trigger\":null}}"));
        string facts = Facts.Replace("\"availableResearch\":[", $"\"availableResearch\":[{research},");
        Assert.Equal(26, DecisionShadow.Build(facts, null, Goal(GoalCategory.Research, "automation", GoalUnit.Completion))
            .Questions["next_goal"].Criteria!.Count);
    }
}
