using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceMemoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-resource-memory-" + Guid.NewGuid().ToString("N"));
    public ResourceMemoryTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task RestartRetainsHistoricalResourcesAndSurveyWithoutAddingThemToCurrentMap()
    {
        var map = Map();
        var session = new RuntimeSession(directory, "factorio.exe", "config.ini", "mods", "save.zip", 0, 1, 2, "fixture-only", "session", "world", 1, true);
        await new SpatialClient(new SessionGameClient(session, new MapGame(map))).CaptureAsync();
        var away = map with { CollectedTick = 200, Bounds = new(new(80, 80), new(105, 105)), Entities = [],
            Actor = map.Actor with { Position = new(90, 90) } };
        away = away with { Scope = away.Scope with { SessionId = "resumed", Generation = 3 } };
        var restarted = new SessionGameClient(session with { SessionId = "resumed" }, new MapGame(away));
        var memory = await restarted.ReadResourceMemoryAsync(away);
        Assert.Equal("copper-ore", Assert.Single(memory.Resources).Name);
        Assert.Equal(100, memory.Resources[0].ObservedTick);
        Assert.NotEmpty(memory.SurveyedCells);
        Assert.Empty(away.Entities);
    }

    [Fact]
    public void CompleteReobservationRemovesDisappearedDepositButKeepsDistantHistory()
    {
        var map = Map();
        var memory = ResourceMemorySnapshot.Empty(map).Merge(map);
        var away = map with { CollectedTick = 110, Bounds = new(new(80, 80), new(105, 105)), Entities = [] };
        Assert.Single(memory.Merge(away).Resources);
        Assert.Empty(memory.Merge(map with { CollectedTick = 120, Entities = [] }).Resources);
    }

    [Fact]
    public void OtherWorldSurfaceAndFutureHistoryAreRejected()
    {
        var map = Map();
        var memory = ResourceMemorySnapshot.Empty(map).Merge(map);
        Assert.Throws<InvalidDataException>(() => memory.Merge(map with { Scope = map.Scope with { WorldId = "other" } }));
        Assert.Throws<InvalidDataException>(() => memory.Merge(map with { SurfaceIndex = 2 }));
        Assert.Throws<InvalidDataException>(() => memory.Merge(map with { CollectedTick = 99 }));
        Assert.Throws<InvalidDataException>(() => memory.Merge(map with { Coverage = map.Coverage with { Complete = false } }));
    }

    [Fact]
    public void MemoryRecordsResourcesWithoutEnemyPositionsOrStockCounts()
    {
        var map = Map();
        var enemy = new SpatialEntity("enemy", "wall", new(4, 4), new(new(3.5, 3.5), new(4.5, 4.5)), 0, "enemy");
        var memory = ResourceMemorySnapshot.Empty(map).Merge(map with { Entities = [.. map.Entities, enemy] });
        Assert.Single(memory.Resources);
        Assert.DoesNotContain("enemy", System.Text.Json.JsonSerializer.Serialize(memory, Protocol.Json));
    }

    [Fact]
    public void ProcessingAreaIsOnlyAHintForAnUnsurveyedKnownConsumer()
    {
        var map = Map();
        var recipe = new NativeRecipe("copper-plate", true, "smelting", 3.2,
            [new("copper-ore", "item", 1)], [new("copper-plate", "item", 1)], true);
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [recipe], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        var memory = ResourceMemorySnapshot.Empty(map);
        (string Id, string? Recipe, MapPosition Position)[] known = [("copper-furnace", recipe.Name, new(80, 80)), ("unrelated", null, new(70, 70))];
        Assert.Equal("copper-furnace", memory.ProcessingAreaHint("copper-ore", catalog, known, map)!.EntityId);
        Assert.Null(memory.ProcessingAreaHint("iron-ore", catalog, known, map));
        Assert.Null((memory with { SurveyedCells = [new(20, 20)] }).ProcessingAreaHint("copper-ore", catalog, known, map));
        Assert.Empty(memory.Resources);
    }

    internal static SpatialSnapshot Map()
    {
        var map = SpatialPlannerTests.Map([]);
        var prototypes = map.Prototypes.ToDictionary();
        prototypes["copper-ore"] = new("copper-ore", "resource", new(new(-.4, -.4), new(.4, .4)), new([], false, false, false), 1, 1);
        return map with { Prototypes = prototypes, Entities = [new("ore", "copper-ore", new(4, 4), new(new(3.6, 3.6), new(4.4, 4.4)), 0, "neutral", Amount: 500)] };
    }
    private sealed class MapGame(SpatialSnapshot map) : IGameClient
    {
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(map)));
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
