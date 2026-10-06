using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ExplorationThreatInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyANewlyObservedEnvelopeMayInvalidateAnExplorationPoint(bool previouslyKnown)
    {
        var game = new Game();
        var map = game.Map();
        var origin = previouslyKnown ? map : map with { StationaryThreats = [] };
        var journal = new Journal();
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        await using var controller = new SpatialController(game, journal);
        var waypoint = new ExplorationWaypoint(new(27, 40), map.CollectedTick);
        if (previouslyKnown)
        {
            var refusal = await Assert.ThrowsAsync<NavigationPlanningException>(() => controller.NavigateExplorationAsync(waypoint, origin, catalog, default));
            Assert.Equal(RouteStatus.NoRouteOnKnownGrid, refusal.Status);
            Assert.DoesNotContain("exploration-waypoint-threat-invalidated", journal.Types);
        }
        else
        {
            Assert.False(await controller.NavigateExplorationAsync(waypoint, origin, catalog, default));
            Assert.Contains("exploration-waypoint-threat-invalidated", journal.Types);
        }
        Assert.Equal(0, game.Moves);
    }

    private sealed class Game : IGameClient
    {
        private long tick = 100;
        public int Moves { get; private set; }
        public SpatialSnapshot Map()
        {
            var basis = SpatialPlannerTests.Map([]);
            return basis with { CollectedTick = tick, Actor = basis.Actor with { Position = new(27.5, 41.5) },
                Bounds = new(new(20, 30), new(41, 52)), Rows = Enumerable.Range(30, 22).Select(y => new TileRun(20, y, 21, "grass")).ToArray(),
                StationaryThreats = [new("newly-seen-worm", new(0, 0), 25, tick)] };
        }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var map = Map();
            object data;
            if (request.Action == "spatial") data = map with { Coverage = map.Coverage with { Radius = request.Arguments.GetProperty("radius").GetInt32() } };
            else if (request.Action == "production_catalog") data = new ProductionCatalog(map.Scope, tick, [], new Dictionary<string, NativeItem>(),
                new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
            else if (request.Action == "observe") data = new { map.Scope, collectedTick = tick,
                coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                    enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                    health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>() };
            else if (request.Action == "submit")
            {
                var operation = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                if (operation.Kind == "move") Moves++;
                Assert.Equal("wait", operation.Kind);
                long accepted = tick;
                tick += operation.Args.GetProperty("ticks").GetInt64();
                data = new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = accepted, updatedTick = tick,
                    effects = new { position = map.Actor.Position, inventoryDelta = new { }, elapsedTicks = tick - accepted } };
            }
            else throw new InvalidOperationException(request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken cancellationToken = default)
        { Types.Add(type); return Task.CompletedTask; }
    }
}
