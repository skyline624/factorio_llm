using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResearchPrerequisiteControllerTests
{
    [Fact]
    public async Task ExistingStockDoesNotCountAsTheUnmetCraftTrigger()
    {
        var game = new ResearchGame();
        var producer = new Producer(game, unlock: true);
        var result = await new ResearchPrerequisiteController(game, producer, new Journal()).RunAsync("automation");
        Assert.Equal(("lab", 3), Assert.Single(producer.Requests));
        Assert.Equal("ready-for-lab", result.Status);
        Assert.Equal("science", Assert.Single(result.VerifiedTriggers));
        Assert.Equal("research", result.Next.Kind);
    }

    [Fact]
    public async Task IncreasedStockWithoutNativeUnlockStopsInsteadOfClaimingSuccessOrCraftingAgain()
    {
        var game = new ResearchGame();
        var producer = new Producer(game, unlock: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ResearchPrerequisiteController(game, producer, new Journal()).RunAsync("automation"));
        Assert.Single(producer.Requests);
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Producer(ResearchGame game, bool unlock) : IStockGoalExecutor
    {
        public List<(string Item, int Count)> Requests { get; } = [];
        public Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default)
        {
            Requests.Add((item, targetStock));
            game.Unlocked = unlock;
            return Task.FromResult(new StockGoalResult("test", item, targetStock, 2, targetStock, 1, 2));
        }
    }

    private sealed class ResearchGame : IGameClient
    {
        public bool Unlocked { get; set; }
        private long tick;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            tick++;
            object data;
            if (request.Action == "observe")
                data = new { scope = new ActorScope("world", "session", "actor", 1, 1), agent = new { inventory = new Dictionary<string, long> { ["lab"] = 2 } } };
            else if (request.Action == "technologies")
            {
                string name = request.Arguments.GetProperty("name").GetString()!;
                NativeTechnology technology = name == "science"
                    ? new(name, true, Unlocked, !Unlocked, [], [], 1, 0,
                        Protocol.ToElement(new { type = "craft-item", item = new { name = "lab" }, count = 1 }))
                    : new(name, true, false, Unlocked, ["science"], [new("pack", 1)], 10, 600);
                data = new { items = new[] { technology }, total = 1, offset = 0, limit = 1, collectedTick = tick, complete = true };
            }
            else throw new InvalidOperationException("Unexpected mutation outside the stock executor.");
            return Task.FromResult(new GameResponse(Protocol.Version, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
