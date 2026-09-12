using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResearchGoalExecutorTests
{
    [Fact]
    public async Task CompletesPrerequisiteThenTargetUsingFreshNativeFlags()
    {
        var game = new ResearchGame();
        var steps = new Steps(game, true);
        var result = await new ResearchGoalExecutor(game, new Journal(), steps).RunAsync("target");
        Assert.True(result.Researched);
        Assert.Equal(new[] { "prerequisite", "target" }, result.CompletedTechnologies);
        Assert.Equal(new[] { "prerequisite", "target" }, steps.Executed);
    }

    [Fact]
    public async Task ReturnedStepWithoutNativeCompletionIsNotRepeatedOrReportedAsSuccess()
    {
        var game = new ResearchGame();
        var steps = new Steps(game, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResearchGoalExecutor(game, new Journal(), steps).RunAsync("target"));
        Assert.Equal(new[] { "prerequisite" }, steps.Executed);
    }

    [Fact]
    public async Task AlreadyResearchedTargetRequiresNoStageExecution()
    {
        var game = new ResearchGame();
        game.Finished.UnionWith(["target", "prerequisite"]);
        var steps = new Steps(game, true);
        Assert.True((await new ResearchGoalExecutor(game, new Journal(), steps).RunAsync("target")).Researched);
        Assert.Empty(steps.Executed);
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Steps(ResearchGame game, bool complete) : IResearchStepExecutor
    {
        public List<string> Executed { get; } = [];
        public Task ExecuteAsync(TechnologyStep step, CancellationToken token)
        {
            Executed.Add(step.Technology);
            if (complete) game.Finished.Add(step.Technology);
            return Task.CompletedTask;
        }
    }
    private sealed class ResearchGame : IGameClient
    {
        public HashSet<string> Finished { get; } = [];
        private long tick;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            tick++;
            var scope = new ActorScope("world", "session", "actor", 1, 1);
            if (request.Action == "observe") return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new { scope })));
            if (request.Action != "technologies") throw new InvalidOperationException("Unexpected mutation in dependency reader.");
            string name = request.Arguments.GetProperty("name").GetString()!;
            var technology = new NativeTechnology(name, true, Finished.Contains(name), name == "prerequisite" || Finished.Contains("prerequisite"),
                name == "prerequisite" ? [] : ["prerequisite"], [new("red", 1)], 1, 60);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new
            { items = new[] { technology }, total = 1, offset = 0, limit = 1, collectedTick = tick, complete = true })));
        }
    }
}
