using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicResearchTests
{
    [Fact]
    public async Task ResearchProposalUsesScientificExecutionAndFreshCompletionEvidence()
    {
        var game = new Game();
        var planner = new Planner();
        var result = await new StrategicProductionController(game, planner, new Journal()).RunOnceAsync();
        Assert.Null(result.Production);
        Assert.True(result.Research!.Researched);
        Assert.Equal("automation", result.Research.Target);
        Assert.DoesNotContain("submit", game.Actions);
        Assert.Contains("nativeTechnologyIdentifiers", planner.Context!.Facts);
        Assert.DoesNotContain("position", planner.Context.Facts);
    }
    [Fact]
    public async Task UnsupportedProposalReturnsExplicitFeedbackWithoutGoalExecution()
    {
        var game = new Game();
        var result = await new StrategicProductionController(game, new Planner(true), new Journal()).RunOnceAsync();
        Assert.NotNull(result.UnsupportedReason);
        Assert.Null(result.Production);
        Assert.Null(result.Research);
        Assert.DoesNotContain("submit", game.Actions);
    }
    private sealed class Planner(bool unsupported = false) : IStrategicPlanner
    {
        public StrategicContext? Context { get; private set; }
        public Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            return Task.FromResult(new GoalProposal(context.ObservationId, "Complete automation", unsupported ? GoalCategory.Other : GoalCategory.Research,
                "automation", 1, GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)));
        }
    }
    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Game : IGameClient
    {
        public List<string> Actions { get; } = [];
        private long tick;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            tick++;
            var scope = new ActorScope("world", "session", "actor", 1, 1);
            object data = request.Action switch
            {
                "observe" => new { scope, snapshotId = tick, agent = new { alive = true, health = 250, inventory = new Dictionary<string, long>(), ammoRounds = 100 },
                    environment = new { }, enemies = Array.Empty<object>(), resources = Array.Empty<object>(), entities = Array.Empty<object>() },
                "production_catalog" => new ProductionCatalog(scope, tick, [], new Dictionary<string, NativeItem>(), new Dictionary<string, NativeMaterial[]>(),
                    new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>()),
                "technologies" => new { items = new[] { new NativeTechnology("automation", true, true, false, [], [new("red", 1)], 10, 600) },
                    total = 1, offset = 0, limit = request.Arguments.TryGetProperty("name", out _) ? 1 : 100, collectedTick = tick, complete = true },
                _ => throw new InvalidOperationException("Unexpected execution outside research completion reading.")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
