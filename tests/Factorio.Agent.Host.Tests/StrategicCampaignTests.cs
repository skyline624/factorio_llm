using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicCampaignTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-strategy-" + Guid.NewGuid().ToString("N"));
    public StrategicCampaignTests() => Directory.CreateDirectory(directory);
    private string Memory => Path.Combine(directory, "strategy.json");

    [Fact]
    public async Task DiscoveryFeedbackPreservesTheObservationProofWithoutSendingItsPositionOrNativeEntityId()
    {
        var discovery = new ResourceDiscoveryResult("crude-oil", "private-resource-coordinate-id", new(12345, 67890), 100000, 10, 20, 3);
        var completed = Completed() with { Goal = Completed().Goal with { Category = GoalCategory.Exploration,
            Target = "crude-oil", Quantity = 1, Unit = GoalUnit.Completion }, Research = null, Discovery = discovery };
        var runner = new Runner(() => completed);
        await new StrategicCampaignController(new Game(), runner, Memory).RunAsync(2);
        using var feedback = JsonDocument.Parse(runner.History[1]!);
        var measured = feedback.RootElement.GetProperty("discovery");
        Assert.Equal("crude-oil", measured.GetProperty("resource").GetString());
        Assert.Equal(20, measured.GetProperty("endTick").GetInt64());
        Assert.Equal("current-native-local-resource", measured.GetProperty("evidence").GetString());
        Assert.Contains("not proven", measured.GetProperty("interpretation").GetString());
        Assert.DoesNotContain("12345", runner.History[1]);
        Assert.DoesNotContain("67890", runner.History[1]);
        Assert.DoesNotContain(discovery.EntityId, runner.History[1]);
    }

    [Fact]
    public async Task InstalledDefenseEvidenceSurvivesIntoTheNextStrategicGoal()
    {
        var defense = new DefenseDeploymentResult("gun-turret", 2, 2, 1, 2, 10, 20, 1, 4, 1, ["existing", "new"], ["exposed"]);
        var completed = Completed() with { Goal = Completed().Goal with { Category = GoalCategory.Defense, Target = "gun-turret", Quantity = 2, Unit = GoalUnit.Items },
            Research = null, Defense = defense };
        var runner = new Runner(() => completed);
        await new StrategicCampaignController(new Game(), runner, Memory).RunAsync(2);
        using var feedback = JsonDocument.Parse(runner.History[1]!);
        var measured = feedback.RootElement.GetProperty("defense").Deserialize<DefenseDeploymentResult>(Protocol.Json)!;
        Assert.Equal(defense.ReadyCount, measured.ReadyCount);
        Assert.Equal(defense.EndTick, measured.EndTick);
        Assert.Equal(defense.ReadyIds, measured.ReadyIds);
        Assert.Equal(defense.ExposedIds, measured.ExposedIds);
        Assert.True(runner.History[1]!.Length <= 4000);
    }

    [Fact]
    public async Task CarriesVerifiedPreviousOutcomeAcrossIterationsAndStopsOnNativeRocket()
    {
        var game = new Game();
        var runner = new Runner(() => { if (++game.Goals == 2) game.Rockets = 1; return Completed(); });
        var result = await new StrategicCampaignController(game, runner, Memory).RunAsync(10);
        Assert.True(result.RocketLaunched);
        Assert.Equal(2, result.GoalsExecuted);
        Assert.Null(runner.History[0]);
        Assert.Contains("automation", runner.History[1]);
        Assert.Contains("researched", runner.History[1]);
    }

    [Fact]
    public async Task PersistsCompletedHistoryAcrossProcessInstancesWithoutClaimingRocket()
    {
        var game = new Game();
        var first = await new StrategicCampaignController(game, new Runner(Completed), Memory).RunAsync(1);
        Assert.False(first.RocketLaunched);
        game.Session = "resumed";
        var next = new Runner(Completed);
        await new StrategicCampaignController(game, next, Memory).RunAsync(1);
        Assert.Contains("automation", Assert.Single(next.History));
    }

    [Fact]
    public async Task ExecutionTimeoutStopsAndPersistedUncertainAttemptPreventsBlindRestart()
    {
        var game = new Game();
        var failed = new Runner(() => throw new TimeoutException("Outcome unknown"));
        await Assert.ThrowsAsync<TimeoutException>(() => new StrategicCampaignController(game, failed, Memory).RunAsync(10));
        Assert.Single(failed.History);
        var retry = new Runner(Completed);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StrategicCampaignController(game, retry, Memory).RunAsync(10));
        Assert.Empty(retry.History);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsMemoryFromAnotherWorldOrFutureTick(bool otherWorld)
    {
        var game = new Game();
        await new StrategicCampaignController(game, new Runner(Completed), Memory).RunAsync(1);
        if (otherWorld) game.World = "different"; else game.Tick = -100;
        var runner = new Runner(Completed);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StrategicCampaignController(game, runner, Memory).RunAsync(1));
        Assert.Empty(runner.History);
    }

    [Fact]
    public async Task NeverStartsGoalWhileNativeOperationIsStillRunning()
    {
        var game = new Game { OperationStatus = "running" };
        var runner = new Runner(Completed);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StrategicCampaignController(game, runner, Memory).RunAsync(1));
        Assert.Empty(runner.History);
    }

    [Fact]
    public async Task RepeatedGoalsReturnFeedbackThenStopWithAnExplicitReason()
    {
        var runner = new Runner(() => Completed() with { Research = null, UnsupportedReason = "Unsupported trigger" });
        var result = await new StrategicCampaignController(new Game(), runner, Memory).RunAsync(10);
        Assert.False(result.RocketLaunched);
        Assert.Equal("repeated-goal", result.StopReason);
        Assert.Equal(3, runner.History.Count);
        Assert.Contains("Unsupported trigger", runner.History[1]);
    }

    [Fact]
    public async Task RejectsIdentityChangeInsideOneExecutionEvenWhenRestartWouldBeAllowed()
    {
        var game = new Game();
        var runner = new Runner(() => { game.Session = "other"; return Completed(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => new StrategicCampaignController(game, runner, Memory).RunAsync(1));
    }

    [Fact]
    public async Task SuccessfulGoalWithLargeEvidenceStillCommitsBoundedFeedback()
    {
        string[] ids = Enumerable.Range(0, 400).Select(i => $"gun-turret:{i}").ToArray();
        var defense = new DefenseDeploymentResult("gun-turret", 400, 400, 0, 400, 10, 20, 1, 4, 1, ids, ids);
        var completed = Completed() with { Goal = Completed().Goal with { Category = GoalCategory.Defense, Target = "gun-turret", Quantity = 400, Unit = GoalUnit.Items },
            Research = null, Defense = defense };
        var runner = new Runner(() => completed);
        var result = await new StrategicCampaignController(new Game(), runner, Memory).RunAsync(2);
        Assert.Equal(2, result.GoalsExecuted);
        Assert.True(runner.History[1]!.Length <= 4000);
        Assert.Contains("gun-turret", runner.History[1]);
        Assert.False(JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(Memory), Protocol.Json)!.Pending);
    }

    [Fact]
    public async Task NavigationFailureReachesTheModelAsASpecificCategory()
    {
        var game = new Game { OperationStatus = null };
        var runner = new JournaledRunner(game, Journal, attempt => attempt == 0
            ? throw new NavigationPlanningException(RouteStatus.NoRouteOnKnownGrid, "NoRouteOnKnownGrid: trapped")
            : Completed());
        var result = await new StrategicCampaignController(game, runner, Memory, Journal).RunAsync(2);
        Assert.Equal(2, result.GoalsExecuted);
        Assert.Contains("navigation_blocked", runner.History[1]);
        Assert.Contains("\"consecutiveFailures\":1", runner.History[1]);
    }

    [Fact]
    public async Task ConsecutiveFailuresStopTheCampaignInsteadOfLoopingOnTheModel()
    {
        var game = new Game { OperationStatus = null };
        var runner = new JournaledRunner(game, Journal,
            _ => throw new NavigationPlanningException(RouteStatus.NoRouteOnKnownGrid, "NoRouteOnKnownGrid: trapped"));
        var result = await new StrategicCampaignController(game, runner, Memory, Journal).RunAsync(100);
        Assert.Equal("repeated-failure", result.StopReason);
        Assert.Equal(StrategicCampaignController.MaxConsecutiveFailures, runner.History.Count);
        Assert.False(JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(Memory), Protocol.Json)!.Pending);
    }

    [Fact]
    public async Task SuccessResetsTheConsecutiveFailureCount()
    {
        var game = new Game { OperationStatus = null };
        var runner = new JournaledRunner(game, Journal, attempt => attempt % 2 == 0
            ? throw new NavigationPlanningException(RouteStatus.NoRouteOnKnownGrid, "NoRouteOnKnownGrid: trapped")
            : Completed() with { Goal = Completed().Goal with { Target = $"goal-{attempt}" } });
        var result = await new StrategicCampaignController(game, runner, Memory, Journal).RunAsync(12);
        Assert.Equal("goal-budget", result.StopReason);
        Assert.Equal(12, runner.History.Count);
    }

    [Fact]
    public async Task FactoryMaintenanceRunsBetweenGoalsAndItsFailureDoesNotStopTheCampaign()
    {
        int calls = 0, goal = 0;
        var runner = new Runner(() => Completed() with { Goal = Completed().Goal with { Target = $"goal-{goal++}" } });
        var result = await new StrategicCampaignController(new Game(), runner, Memory, maintenance: _ =>
        {
            calls++;
            throw new InvalidOperationException("Synthetic empty chest");
        }).RunAsync(3);
        Assert.Equal(3, result.GoalsExecuted);
        Assert.Equal(2, calls);
    }

    private string Journal => Path.Combine(directory, "journal.jsonl");
    private static StrategicGoalResult Completed() => new(new("o", "Research automation", GoalCategory.Research,
        "automation", 1, GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)),
        Research: new("automation", true, 1, 2, ["automation"]));
    private sealed class Runner(Func<StrategicGoalResult> action) : IStrategicGoalRunner
    {
        public List<string?> History { get; } = [];
        public Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        { History.Add(previousResult); return Task.FromResult(action()); }
    }
    /// <summary>Records the strategic context like the production runner before its synthetic outcome.</summary>
    private sealed class JournaledRunner(Game game, string journal, Func<int, StrategicGoalResult> action) : IStrategicGoalRunner
    {
        public List<string?> History { get; } = [];
        public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            History.Add(previousResult);
            var writer = new ControllerJournal(journal);
            await writer.AppendAsync("strategic-context", new { observationId = $"o:{game.Tick}",
                facts = JsonSerializer.Serialize(new { observedTick = game.Tick }) }, token);
            await writer.AppendAsync("strategic-goal", new { category = 1, target = "steam-power", quantity = 1, unit = 4 }, token);
            return action(History.Count - 1);
        }
    }
    private sealed class Game : IGameClient
    {
        public int Goals, Rockets;
        public long Tick = 100;
        public string World = "world", Session = "session";
        public string? OperationStatus = "completed";
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("observe", request.Action);
            ++Tick;
            var agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, walking = false, mining = false,
                shooting = false, craftingQueueSize = 0 };
            var scope = new ActorScope(World, Session, "actor", 1, 1);
            var goal = new { rocketsLaunched = Rockets };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Tick, OperationStatus is null
                ? Protocol.ToElement(new { scope, collectedTick = Tick, agent, goal })
                : Protocol.ToElement(new { scope, collectedTick = Tick, agent, goal, operation = new { status = OperationStatus } })));
        }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
