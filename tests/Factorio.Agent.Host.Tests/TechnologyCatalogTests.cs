using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TechnologyCatalogTests
{
    [Fact]
    public async Task CollectsEveryPageWithACollectionInterval()
    {
        var result = await new TechnologyClient(new CatalogGame()).ReadAllAsync();
        Assert.Equal(101, result.Technologies.Count);
        Assert.True(result.EndTick > result.StartTick);
    }
    [Fact]
    public async Task DuplicateTechnologyAcrossPagesIsRejected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new TechnologyClient(new CatalogGame(duplicate: true)).ReadAllAsync());
    [Fact]
    public async Task ChangedActorAfterPaginationIsRejected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new TechnologyClient(new CatalogGame(changedActor: true)).ReadAllAsync());

    private sealed class CatalogGame(bool duplicate = false, bool changedActor = false) : IGameClient
    {
        private long tick;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            tick++;
            object data;
            if (request.Action == "observe")
                data = new { scope = new ActorScope("world", "session", "actor", 1, changedActor && tick > 1 ? 2 : 1) };
            else
            {
                int offset = request.Arguments.GetProperty("offset").GetInt32();
                var items = Enumerable.Range(offset, offset == 0 ? 100 : 1).Select(n =>
                    new NativeTechnology("tech-" + (duplicate && offset > 0 ? 0 : n), true, false, true, [], [new("red", 1)], 1, 60)).ToArray();
                data = new { items, offset, total = 101, limit = 100, collectedTick = tick, complete = offset > 0 };
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
