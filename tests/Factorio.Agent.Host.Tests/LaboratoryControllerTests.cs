using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LaboratoryControllerTests
{
    [Fact]
    public async Task AnotherSelectedTechnologyIsPreservedBeforeAnyProductionOrBuilding()
    {
        var game = new ResearchGame(otherScope: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LaboratoryController(game, new Journal()).RunAsync("automation"));
        Assert.DoesNotContain(game.Actions, a => a is "submit" or "production_catalog");
    }

    [Fact]
    public async Task IdentityChangeDuringScientificObservationPreventsMutation()
    {
        var game = new ResearchGame(otherScope: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LaboratoryController(game, new Journal()).RunAsync("automation"));
        Assert.DoesNotContain(game.Actions, a => a is "submit" or "production_catalog");
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ResearchGame(bool otherScope) : IGameClient
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
                "observe" => new { scope },
                "technologies" => new { items = new[] { new NativeTechnology("automation", true, false, true, [], [new("red", 1)], 10, 600) },
                    total = 1, offset = 0, limit = 1, collectedTick = tick, complete = true },
                "research_state" => new ResearchSnapshot(otherScope ? scope with { Generation = 2 } : scope, tick, 1, "automation", false, 0,
                    new Dictionary<string, LaboratoryPrototype>(), [], new Dictionary<string, double>(), true, true,
                    new Dictionary<string, long>(), new Dictionary<string, double>(), otherScope ? null : "different-technology"),
                _ => throw new InvalidOperationException("Unexpected production or mutation before reconciliation.")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
