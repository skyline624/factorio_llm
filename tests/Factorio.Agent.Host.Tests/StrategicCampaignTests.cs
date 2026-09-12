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

    private static StrategicGoalResult Completed() => new(new("o", "Research automation", GoalCategory.Research,
        "automation", 1, GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)),
        Research: new("automation", true, 1, 2, ["automation"]));
    private sealed class Runner(Func<StrategicGoalResult> action) : IStrategicGoalRunner
    {
        public List<string?> History { get; } = [];
        public Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        { History.Add(previousResult); return Task.FromResult(action()); }
    }
    private sealed class Game : IGameClient
    {
        public int Goals, Rockets;
        public long Tick = 100;
        public string World = "world", Session = "session", OperationStatus = "completed";
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("observe", request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, ++Tick, Protocol.ToElement(new
            {
                scope = new ActorScope(World, Session, "actor", 1, 1),
                agent = new { alive = true, controlMode = "ai" }, goal = new { rocketsLaunched = Rockets },
                operation = new { status = OperationStatus }
            })));
        }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
