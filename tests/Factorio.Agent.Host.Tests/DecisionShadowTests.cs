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
    public void StateStaysCompactForTheFourThousandTokenWindow()
    {
        var question = DecisionShadow.Build(Facts, "{\"goal\":{\"target\":\"automation\"},\"executionFailure\":\"navigation_blocked\"}",
            Goal(GoalCategory.Research, "automation", GoalUnit.Completion));
        Assert.True(question.State.Length <= 3000);
        using var state = JsonDocument.Parse(question.State);
        Assert.Equal(250, state.RootElement.GetProperty("health").GetDouble());
        Assert.Contains("navigation_blocked", question.State);
    }

    [Fact]
    public void AnAlreadyAutomatedPackIsOfferedOnlyWhenTheProposalTargetsIt()
    {
        string facts = Facts.Replace("{\"recipe\":\"iron-gear-wheel\",\"cells\":1}", "{\"recipe\":\"automation-science-pack\",\"cells\":2}");
        Assert.DoesNotContain("automate:automation-science-pack",
            DecisionShadow.Build(facts, null, Goal(GoalCategory.Research, "automation", GoalUnit.Completion)).Questions["next_goal"].Criteria!.Keys);
        var proposed = DecisionShadow.Build(facts, null, Goal(GoalCategory.Production, "automation-science-pack", GoalUnit.ItemsPerMinute));
        Assert.Equal("automate:automation-science-pack", proposed.ProposedOption);
        var text = proposed.Questions["next_goal"].Criteria!["automate:automation-science-pack"]!;
        Assert.StartsWith("Increase the existing", text);
        Assert.Contains("2 cells", text);
    }

    [Fact]
    public void ResearchOptionsNameWhatTheyUnlock()
    {
        string lamp = "{\"name\":\"lamp\",\"count\":10,\"ingredients\":[{\"name\":\"automation-science-pack\",\"amount\":1}],\"trigger\":null}";
        string facts = Facts.Replace(lamp, lamp.Replace("\"trigger\":null", "\"trigger\":null,\"unlocks\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]"));
        var options = DecisionShadow.Build(facts, null, Goal(GoalCategory.Research, "lamp", GoalUnit.Completion)).Questions["next_goal"].Criteria!;
        Assert.Equal("Research lamp: unlocks a, b, c, d, e, ... (10 units of automation-science-pack)", options["research:lamp"]);
        Assert.Equal("Research automation: 10 units of automation-science-pack; on the rocket silo path", options["research:automation"]);
    }

    [Fact]
    public void ExposedIndustryWithoutAttacksIsNotPresentedAsAThreat()
    {
        var question = DecisionShadow.Build(Facts.Replace("\"exposedIndustrialAnchors\":7", "\"exposedIndustrialAnchors\":43"), null,
            Goal(GoalCategory.Research, "automation", GoalUnit.Completion));
        Assert.Contains("uncovered industry alone is not a threat", question.Questions["next_goal"].Instructions);
        Assert.Contains("uncovered industry alone is not a threat", question.Questions["proposal_sound"].Instructions);
        Assert.Contains("0 recent attacks", question.Questions["next_goal"].Criteria!["defense:gun-turret"]);
        Assert.Equal("research:automation", question.ProposedOption);
        Assert.DoesNotContain("exposedIndustrialAnchors", question.State);
    }

    [Fact]
    public void ARawItemProductionProposalIsComparable()
    {
        var goal = Goal(GoalCategory.Production, "iron-plate", GoalUnit.ItemsPerMinute);
        Assert.Equal("automate:iron-plate", DecisionShadow.Build(Facts, null, goal).ProposedOption);
        string facts = Facts.Replace("\"assemblerCells\":", "\"rawSupply\":[{\"item\":\"iron-plate\",\"readyCells\":1,\"perMinute\":30.04}],\"assemblerCells\":");
        var question = DecisionShadow.Build(facts, null, goal);
        Assert.Equal("automate:iron-plate", question.ProposedOption);
        Assert.Equal("Raise persistent iron-plate supply (now 30.04/min)", question.Questions["next_goal"].Criteria!["automate:iron-plate"]);
    }

    [Fact]
    public void AHugeFactsDocumentStillYieldsABoundedState()
    {
        string cells = string.Join(",", Enumerable.Range(0, 80).Select(i => $"{{\"recipe\":\"recipe-number-{i}\",\"cells\":{i}}}"));
        string researched = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"a-rather-long-technology-name-{i}\""));
        string inventory = string.Join(",", Enumerable.Range(0, 60).Select(i => $"\"some-long-item-name-{i}\":{i + 1}"));
        string facts = Facts.Replace("{\"recipe\":\"iron-gear-wheel\",\"cells\":1}", cells)
            .Replace("\"researchedTechnologies\":[", $"\"researchedTechnologies\":[{researched},")
            .Replace("\"inventory\":{", $"\"inventory\":{{{inventory},");
        string previous = "{\"goal\":{\"category\":0,\"target\":\"iron-plate\",\"quantity\":30},\"unsupportedReason\":\"" + new string('x', 900)
            + "\",\"logistics\":{\"shortfall\":{\"copper-plate\":25},\"powerStarved\":false}}";
        var question = DecisionShadow.Build(facts, previous, Goal(GoalCategory.Research, "automation", GoalUnit.Completion));
        Assert.True(question.State.Length <= 3000, question.State.Length.ToString());
        using var state = JsonDocument.Parse(question.State);
        Assert.Equal(25, state.RootElement.GetProperty("previous").GetProperty("shortfall").GetProperty("copper-plate").GetDouble());
    }

    [Theory]
    [InlineData("{\"observedTick\":9,\"agent\":{\"alive\":true},\"availableSolidRecipes\":[\"gun-turret\"],\"nativeDefenseItems\":[\"gun-turret\"]}", null)]
    [InlineData("{\"observedTick\":9,\"availableResearch\":[{\"name\":\"lamp\"}],\"automatedFactory\":{\"available\":true}}", "[1,2]")]
    [InlineData("{\"observedTick\":9}", "not json")]
    public void OlderOrOddFactsAndOutcomesDoNotThrow(string facts, string? previous)
    {
        var question = DecisionShadow.Build(facts, previous, Goal(GoalCategory.Research, "lamp", GoalUnit.Completion));
        Assert.Contains("none", question.Questions["next_goal"].Criteria!.Keys);
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
