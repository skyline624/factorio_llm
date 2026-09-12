using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Infrastructure.Tests;

public sealed class TechnologyClientTests
{
    [Fact]
    public async Task ChangedActorInvalidatesTheWholeDependencyObservation()
    {
        var game = new CatalogGame(changeActor: true, wrongName: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TechnologyClient(game).ReadDependenciesAsync("science"));
    }

    [Fact]
    public async Task ATechnologyWithAnotherNameCannotSatisfyTheRequestedDependency()
    {
        var game = new CatalogGame(changeActor: false, wrongName: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TechnologyClient(game).ReadDependenciesAsync("science"));
    }

    private sealed class CatalogGame(bool changeActor, bool wrongName) : IGameClient
    {
        private long tick = 0;
        private int observations;

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            tick++;
            object data;
            if (request.Action == "observe")
            {
                observations++;
                data = new { scope = new ActorScope("world", "session", "actor", changeActor && observations > 1 ? 2 : 1, 1) };
            }
            else if (request.Action == "technologies")
            {
                var technology = new NativeTechnology(wrongName ? "other" : "science", true, false, true, [], [], 1, 600);
                data = new { items = new[] { technology }, total = 1, offset = 0, limit = 1, collectedTick = tick, complete = true };
            }
            else throw new InvalidOperationException("Technology collection must only read native observations.");
            return Task.FromResult(new GameResponse(Protocol.Version, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
