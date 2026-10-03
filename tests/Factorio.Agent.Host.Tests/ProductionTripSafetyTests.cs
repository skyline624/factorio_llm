using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionTripSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistantStockCollectionAvoidsRecentOwnDeathZonesBeforeMoving(bool recentDeath)
    {
        var game = new Game(recentDeath);
        var journal = new Journal();
        await Assert.ThrowsAsync<SegmentBoundaryException>(() => new ProductionController(game, journal).CollectAvailableAsync("iron-plate", 1));
        Assert.Equal(1, game.DeathReads);
        Assert.Equal(!recentDeath, DangerZones.Covers(game.Death, journal.Waypoint!));
        Assert.All(game.Calls, action => Assert.Contains(action, new[] { "observe", "production_catalog", "spatial" }));
    }

    private sealed class SegmentBoundaryException : Exception;

    private sealed class Journal : IControllerJournal
    {
        public MapPosition? Waypoint { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "travel-segment")
            {
                Waypoint = Protocol.ToElement(data).GetProperty("waypoint").Deserialize<MapPosition>(Protocol.Json);
                throw new SegmentBoundaryException(); // Inspect the real production travel plan before any mutation.
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Game(bool recentDeath) : IGameClient, IDangerZoneReader
    {
        private readonly SpatialSnapshot map = FactoryMaps.Grass(48);
        public NativeDeathTransition Death { get; } = new(1, 50, 1, 1, new(40, 0));
        public int DeathReads { get; private set; }
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default)
        {
            DeathReads++;
            return Task.FromResult<IReadOnlyList<NativeDeathTransition>>(recentDeath ? [Death] : []);
        }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "production_catalog" => Catalogs.Raw() with { Scope = map.Scope, CollectedTick = map.CollectedTick },
                "spatial" => map,
                "observe" => new
                {
                    scope = map.Scope, collectedTick = map.CollectedTick,
                    coverage = new { knownInventoriesComplete = true, atomic = true, collectionStartTick = map.CollectedTick,
                        collectionEndTick = map.CollectedTick, enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", position = map.Actor.Position, health = 250,
                        stopUnconfirmed = false, inventory = new Dictionary<string, long>(), weapon = new { ready = false, rounds = 0 } },
                    enemies = Array.Empty<object>(),
                    entities = new[] { new { id = "known-output", name = "iron-chest", position = new MapPosition(100, 0), recipe = (string?)null,
                        inventories = new { output = new { items = new Dictionary<string, long> { ["iron-plate"] = 1 } } } } }
                },
                _ => throw new InvalidOperationException($"Unexpected native mutation: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(data)));
        }
    }
}
