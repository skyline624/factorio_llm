using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceTravelCanKeepDeathZonesEnabledForAnExplicitHistoricalDestination(bool avoid)
    {
        var game = new DestinationGame();
        await using var controller = new SpatialController(game, new Journal());
        var waypoint = await controller.FindExplorationWaypointAsync(new ExplorationPlanner(), Catalogs.Raw(), "", new(40, 0),
            avoidDestinationDeathZones: avoid);
        Assert.Equal(avoid ? 1 : 0, game.DeathReads);
        Assert.Equal(!avoid, DangerZones.Covers(game.Death, waypoint.Position));
    }

    [Fact]
    public async Task ACurrentLocalNativeOilDepositCompletesWithoutAnyMutation()
    {
        var game = new DiscoveryGame([Deposit("native-oil", new(5.5, .5))]);
        var journal = new Journal();
        var result = await new ResourceDiscoveryController(game, journal).RunAsync("crude-oil");
        Assert.Equal("native-oil", result.EntityId);
        Assert.Equal(new(5.5, .5), result.Position);
        Assert.Equal(100000, result.NativeAmount);
        Assert.Equal(0, result.SearchSteps);
        Assert.Equal(["production_catalog", "spatial"], game.Calls);
        Assert.Contains("resource-discovery-result", journal.Types);
    }

    [Fact]
    public async Task ARecentDeathExcludesTheNearestDepositButKeepsAnObservedAlternative()
    {
        var game = new DiscoveryGame([Deposit("unsafe", new(5.5, .5)), Deposit("safe", new(43.5, .5))])
            { Deaths = [new(1, 50, 1, 1, new(5.5, .5))] };
        var result = await new ResourceDiscoveryController(game, new Journal()).RunAsync("crude-oil");
        Assert.Equal("safe", result.EntityId);
        Assert.Equal(0, result.SearchSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExpiredOrOtherSurfaceDeathDoesNotBlockTheCurrentDeposit(bool otherSurface)
    {
        var game = new DiscoveryGame([Deposit("oil", new(5.5, .5))])
            { Deaths = [new(1, otherSurface ? 50 : 0, 1, otherSurface ? 2 : 1, new(5.5, .5))],
                Tick = otherSurface ? 100 : DangerZones.LifetimeTicks };
        var result = await new ResourceDiscoveryController(game, new Journal()).RunAsync("crude-oil");
        Assert.Equal("oil", result.EntityId);
    }

    [Fact]
    public async Task ActorIdentityChangeCannotProduceADiscoveryProof()
    {
        var game = new DiscoveryGame([Deposit("oil", new(5.5, .5))]) { WrongScope = true };
        var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResourceDiscoveryController(game, journal).RunAsync("crude-oil"));
        Assert.DoesNotContain("resource-discovery-result", journal.Types);
    }

    [Fact]
    public async Task IncompleteLocalCoverageCannotProduceADiscoveryProof()
    {
        var game = new DiscoveryGame([Deposit("oil", new(5.5, .5))]) { Incomplete = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResourceDiscoveryController(game, new Journal()).RunAsync("crude-oil"));
    }

    [Fact]
    public async Task AChartedHintRequiresTravelAndAnotherNativeObservation()
    {
        var game = new DiscoveryGame([]) { Hint = new("old-oil", "crude-oil", new(100.5, .5), 50, ResourceSighting.Charted) };
        var journal = new Journal();
        await Assert.ThrowsAsync<TravelBoundaryException>(() => new ResourceDiscoveryController(game, journal).RunAsync("crude-oil"));
        Assert.Contains("charted_resources", game.Calls);
        Assert.DoesNotContain("resource-discovery-result", journal.Types);
    }

    [Fact]
    public async Task AStaleHintInsideTheCompleteViewCannotBeUsedAsADepositOrDestination()
    {
        var game = new DiscoveryGame([])
        {
            Hint = new("missing-oil", "crude-oil", new(5.5, .5), 50, ResourceSighting.Charted),
            Deaths = [new(1, 50, 1, 1, new(-100, 0)), new(2, 60, 2, 1, new(-100, 40))]
        };
        var journal = new Journal();
        await Assert.ThrowsAsync<ExplorationTooDangerousException>(() => new ResourceDiscoveryController(game, journal).RunAsync("crude-oil"));
        Assert.DoesNotContain("observe", game.Calls);
        Assert.DoesNotContain("resource-discovery-result", journal.Types);
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("crude oil")]
    public async Task TreesAndAliasesNeverEnterTheDiscoveryExecutor(string target)
    {
        var game = new DiscoveryGame([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResourceDiscoveryController(game, new Journal()).RunAsync(target));
        Assert.Equal(["production_catalog"], game.Calls);
    }

    private static SpatialEntity Deposit(string id, MapPosition point) => new(id, "crude-oil", point,
        new(new(point.X - .1, point.Y - .1), new(point.X + .1, point.Y + .1)), 0, "neutral", 100000);

    private sealed class TravelBoundaryException() : Exception("This test authorizes no travel or mutation.");

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token) { Types.Add(type); return Task.CompletedTask; }
    }

    private sealed class DestinationGame : IGameClient, IDangerZoneReader
    {
        public NativeDeathTransition Death { get; } = new(1, 50, 1, 1, new(40, 0));
        public int DeathReads { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("spatial", request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(FactoryMaps.Grass(48))));
        }
        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default)
        {
            DeathReads++;
            return Task.FromResult<IReadOnlyList<NativeDeathTransition>>([Death]);
        }
    }

    private sealed class DiscoveryGame(IReadOnlyList<SpatialEntity> entities) : IGameClient, IResourceMemoryReader, IDangerZoneReader
    {
        private readonly ProductionCatalog catalog = OilCatalogs.Oil() with { MiningSourceTypes = new Dictionary<string, string>
            { ["crude-oil"] = "resource", ["iron-ore"] = "resource", ["tree"] = "tree" } };
        public List<string> Calls { get; } = [];
        public IReadOnlyList<NativeDeathTransition> Deaths { get; init; } = [];
        public ResourceSighting? Hint { get; init; }
        public bool WrongScope { get; init; }
        public bool Incomplete { get; init; }
        public long Tick { get; init; } = 100;

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            if (request.Action == "charted_resources") return Task.FromResult(new GameResponse(1, request.RequestId, false, Tick, default,
                new("unknown_action", "Synthetic older mod has no chart reading.")));
            if (request.Action == "observe") throw new TravelBoundaryException();
            object data = request.Action switch
            {
                "production_catalog" => catalog with { CollectedTick = Tick },
                "spatial" => Map(),
                _ => throw new InvalidOperationException($"No mutation authorized: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Tick, Protocol.ToElement(data)));
        }

        private SpatialSnapshot Map()
        {
            var map = FactoryMaps.Grass(48, entities);
            var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["crude-oil"] = map.Prototypes["iron-ore"] with { Name = "crude-oil", ResourceCategory = "basic-fluid", InfiniteResource = true } };
            return map with { Scope = WrongScope ? catalog.Scope with { Incarnation = catalog.Scope.Incarnation + 1 } : catalog.Scope,
                CollectedTick = Tick, Prototypes = prototypes, Coverage = map.Coverage with { Complete = !Incomplete } };
        }

        public Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default) =>
            Task.FromResult(ResourceMemorySnapshot.Empty(current) with { Resources = Hint is null ? [] : [Hint] });

        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default) => Task.FromResult(Deaths);
    }
}
