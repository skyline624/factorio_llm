using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceRowSearchTests
{
    [Fact]
    public void CompleteNoSiteDefersOnlyTheRequestedActualResourcesForThisSearch()
    {
        var map = Map() with { Entities = [Ore("used", "coal", new(-8, 0)), Ore("empty", "coal", new(8, 0)) with { Amount = 0 },
            Ore("other", "iron-ore", new(0, 8))] };
        var deferred = new HashSet<string>();
        Assert.Equal(1, ResourceCellBuilder.DeferUnusableResources(ResourceRowSearchStatus.NoSite, map, "coal", deferred));
        Assert.Equal(["used"], deferred);
        Assert.Equal(0, ResourceCellBuilder.DeferUnusableResources(ResourceRowSearchStatus.NoSite, map, "coal", deferred));
        Assert.Equal(1, ResourceCellBuilder.DeferUnusableResources(ResourceRowSearchStatus.NoSite, map, "coal", new HashSet<string>()));
    }

    [Theory]
    [InlineData(ResourceRowSearchStatus.Found)]
    [InlineData(ResourceRowSearchStatus.SearchBudgetExhausted)]
    public void ASearchBudgetOrSuccessfulSiteNeverDefersItsDeposits(ResourceRowSearchStatus status)
    {
        var deferred = new HashSet<string>();
        Assert.Equal(0, ResourceCellBuilder.DeferUnusableResources(status, Map(), "coal", deferred));
        Assert.Empty(deferred);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IncompleteOrNonAtomicTerrainCannotExcludeDeposits(bool atomic, bool complete)
    {
        var map = Map();
        map = map with { Coverage = map.Coverage with { Atomic = atomic, Complete = complete } };
        var deferred = new HashSet<string>();
        Assert.Equal(0, ResourceCellBuilder.DeferUnusableResources(ResourceRowSearchStatus.NoSite, map, "coal", deferred));
        Assert.Empty(deferred);
    }

    [Theory]
    [InlineData(ResourceSighting.Local)]
    [InlineData(ResourceSighting.Charted)]
    public void DeferredDestinationsLeaveResourceHistoryIntact(string origin)
    {
        var map = Map();
        var memory = ResourceMemorySnapshot.Empty(map) with { Resources = [
            new("used", "coal", new(-8, 0), map.CollectedTick, origin),
            new("new", "coal", new(70, 0), map.CollectedTick, origin)] };
        var catalog = Catalogs.Raw() with { Scope = map.Scope };
        Assert.Equal("new", memory.Nearest("coal", catalog, map.Actor.Position, deferredEntityIds: new HashSet<string> { "used" })!.EntityId);
        Assert.Equal("used", memory.Nearest("coal", catalog, map.Actor.Position)!.EntityId);
        Assert.Equal(2, memory.Resources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalResourcesRespectCallerSearchDeferrals(bool defer)
    {
        var map = Map();
        var catalog = Catalogs.Raw() with { Scope = map.Scope };
        var point = new ExplorationPlanner().Choose(map, "coal", catalog,
            deferredResourceIds: defer ? new HashSet<string> { "used" } : null);
        Assert.Equal(defer, point.X > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalSelectionAndLocalSelectionUseTheSameDeferrals(bool defer)
    {
        var map = Map();
        var game = new SearchGame(map, ResourceMemorySnapshot.Empty(map) with { Resources = [
            new("used", "coal", new(-8, 0), map.CollectedTick),
            new("new", "coal", new(40, 0), map.CollectedTick, ResourceSighting.Charted)] });
        var journal = new Journal();
        var point = await new SpatialController(game, journal).FindExplorationWaypointAsync(new ExplorationPlanner(),
            Catalogs.Raw() with { Scope = map.Scope }, "coal", deferredResourceIds: defer ? new HashSet<string> { "used" } : null);
        Assert.Equal(defer, point.Position.X > 0);
        Assert.Equal(defer ? "new" : "used", journal.RememberedId);
        Assert.Equal(["spatial"], game.Calls);
    }

    [Fact]
    public async Task ADeferredDepositDoesNotBypassRecentDeathAvoidanceAtTheAlternative()
    {
        var map = Map();
        var death = new NativeDeathTransition(1, 50, 1, map.SurfaceIndex, new(40, 0));
        var game = new SearchGame(map, ResourceMemorySnapshot.Empty(map) with { Resources = [
            new("used", "coal", new(-8, 0), map.CollectedTick), new("new", "coal", new(40, 0), map.CollectedTick)] }) { Deaths = [death] };
        var journal = new Journal();
        var point = await new SpatialController(game, journal).FindExplorationWaypointAsync(new ExplorationPlanner(),
            Catalogs.Raw() with { Scope = map.Scope }, "coal", deferredResourceIds: new HashSet<string> { "used" });
        Assert.False(DangerZones.Covers(death, point.Position));
        Assert.Null(journal.RememberedId);
    }

    [Fact]
    public async Task AllDeferredDepositsSeekAFrontierWithoutReturningToProcessingAreaHints()
    {
        var map = Map() with { Entities = [Ore("used", "coal", new(-8, 0))] };
        var game = new SearchGame(map, ResourceMemorySnapshot.Empty(map) with { Resources = [new("used", "coal", new(-8, 0), map.CollectedTick)] });
        var planner = new ExplorationPlanner();
        await new SpatialController(game, new Journal()).FindExplorationWaypointAsync(planner,
            Catalogs.Raw() with { Scope = map.Scope }, "coal", deferredResourceIds: new HashSet<string> { "used" });
        Assert.NotNull(planner.Frontier);
        Assert.Equal(["spatial"], game.Calls);
    }

    private static SpatialSnapshot Map() => FactoryMaps.Grass(48, [Ore("used", "coal", new(-8, 0)), Ore("new", "coal", new(40, 0))]);
    private static SpatialEntity Ore(string id, string name, MapPosition point) =>
        new(id, name, point, new(new(point.X - .4, point.Y - .4), new(point.X + .4, point.Y + .4)), 0, "neutral", Amount: 10000);

    private sealed class SearchGame(SpatialSnapshot map, ResourceMemorySnapshot memory) : IGameClient, IResourceMemoryReader, IDangerZoneReader
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<NativeDeathTransition> Deaths { get; init; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            Assert.Equal("spatial", request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(map)));
        }
        public Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default) => Task.FromResult(memory);
        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default) => Task.FromResult(Deaths);
    }

    private sealed class Journal : IControllerJournal
    {
        public string? RememberedId { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "resource-memory-target") RememberedId = Protocol.ToElement(data).GetProperty("remembered").GetProperty("entityId").GetString();
            return Task.CompletedTask;
        }
    }
}
