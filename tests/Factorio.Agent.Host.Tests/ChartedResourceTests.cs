using System.Text.Json;
using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ChartedResourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-charted-" + Guid.NewGuid().ToString("N"));
    public ChartedResourceTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void AReadingOfChartedAndRequestedChunksParsesInDistanceOrder()
    {
        var charted = ChartedResourceSnapshot.Parse(Reply(200, [Oil(), Iron()]));
        Assert.Equal([new ChartedChunk(2, 0), new ChartedChunk(-3, -3)], charted.Deposits.Select(d => d.Chunk));
        Assert.Equal((2, 2, 4), (charted.Coverage.ChartedChunks, charted.Coverage.RequestedChunks, charted.Coverage.ReadChunks));
        Assert.True(charted.Covers(new(80, 24)));
        Assert.False(charted.Covers(new(2000, 0)));
        Assert.Empty(ChartedResourceSnapshot.Parse(Reply(200, [])).Deposits);
    }

    [Theory]
    [InlineData("tick")]
    [InlineData("visibility")]
    [InlineData("sample-outside-chunk")]
    [InlineData("duplicate")]
    [InlineData("unrequested-name")]
    [InlineData("beyond-radius")]
    [InlineData("out-of-order")]
    [InlineData("entities")]
    [InlineData("complete-radius")]
    [InlineData("beyond-truncation")]
    [InlineData("empty-count")]
    [InlineData("amount")]
    [InlineData("more-chunks-than-read")]
    public void InconsistentReadingsAreRefused(string defect)
    {
        var node = JsonSerializer.SerializeToNode(Data(200, [Oil(), Iron()]), Protocol.Json)!.AsObject();
        var deposits = node["deposits"]!.AsArray();
        var coverage = node["coverage"]!.AsObject();
        switch (defect)
        {
            case "tick": node["collectedTick"] = 199; break;
            case "visibility": coverage["visibility"] = "all-generated-chunks"; break;
            case "sample-outside-chunk": deposits[0]!["sample"]!["position"]!["x"] = 10.5; break;
            case "duplicate": deposits[1] = deposits[0]!.DeepClone(); coverage["entities"] = 2; break;
            case "unrequested-name": deposits[1]!["name"] = "coal"; break;
            case "beyond-radius": node["radius"] = 64; coverage["completeRadius"] = 64; break;
            case "out-of-order": (deposits[0], deposits[1]) = (deposits[1]!.DeepClone(), deposits[0]!.DeepClone()); break;
            case "entities": coverage["entities"] = 7; break;
            case "complete-radius": coverage["completeRadius"] = 100; break;
            case "beyond-truncation": coverage["truncated"] = true; coverage["completeRadius"] = 80; break;
            case "empty-count": deposits[0]!["count"] = 0; break;
            case "amount": deposits[0]!["amount"] = 0; break;
            case "more-chunks-than-read": coverage["chartedChunks"] = 0; coverage["requestedChunks"] = 1; break;
        }
        var response = new GameResponse(1, "r", true, 200, JsonSerializer.SerializeToElement(node, Protocol.Json));
        Assert.Throws<InvalidDataException>(() => ChartedResourceSnapshot.Parse(response));
    }

    [Fact]
    public void AFailedReadingIsAnRpcError() => Assert.Throws<GameRpcException>(() =>
        ChartedResourceSnapshot.Parse(new GameResponse(1, "r", false, 200, default, new("actor_dead", "Wait for the character to respawn"))));

    [Fact]
    public void TheRealClientAllowsTheMapReading() => Assert.Contains("charted_resources", Protocol.Actions);

    [Fact]
    public async Task TheClientAsksForNamedResourcesOnlyAndRequiresTheReplyToAnswerIt()
    {
        var game = new ChartedGame { Reading = Reply(200, [Oil(), Iron()]) };
        var client = new ChartedResourceClient(game);
        var charted = await client.CaptureAsync(["crude-oil", "iron-ore"]);
        var request = Assert.Single(game.Requests);
        Assert.Equal("charted_resources", request.Action);
        Assert.Equal(["crude-oil", "iron-ore"], request.Arguments.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(512, request.Arguments.GetProperty("radius").GetInt32());
        Assert.False(request.Arguments.TryGetProperty("center", out _));
        Assert.Equal(2, charted.Deposits.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CaptureAsync(["crude-oil"]));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CaptureAsync(["crude-oil", "iron-ore"], radius: 256));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CaptureAsync(["crude-oil", "iron-ore"], center: new(5, 5)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CaptureAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CaptureAsync(["a", "a"]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CaptureAsync(Enumerable.Range(0, 9).Select(i => $"r{i}").ToArray()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CaptureAsync(["crude-oil"], radius: 31));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CaptureAsync(["crude-oil"], limit: 7));
        Assert.Equal(4, game.Requests.Count);
    }

    [Fact]
    public void AReadingAddsChartedDestinationsWithoutSurveyedGroundOrAmounts()
    {
        var map = ResourceMemoryTests.Map();
        var local = ResourceMemorySnapshot.Empty(map).Merge(map);
        var memory = local.MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil(), Iron()])));
        var oil = Assert.Single(memory.Resources, r => r.Name == "crude-oil");
        Assert.Equal(new ResourceSighting("1:crude-oil:80.5:24.5", "crude-oil", new(80.5, 24.5), 200, ResourceSighting.Charted), oil);
        Assert.Equal(ResourceSighting.Local, memory.Resources.Single(r => r.Name == "copper-ore").Origin);
        Assert.Equal(local.SurveyedCells, memory.SurveyedCells);
        Assert.Equal((200, ResourceMemorySnapshot.CurrentVersion), (memory.LastTick, memory.Version));
        Assert.DoesNotContain("amount", JsonSerializer.Serialize(memory, Protocol.Json));
    }

    [Fact]
    public void ANewReadingForgetsVanishedChartedDepositsOfItsNamesWithinItsCoverageOnly()
    {
        var map = ResourceMemoryTests.Map();
        var memory = ResourceMemorySnapshot.Empty(map).Merge(map).MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil(), Iron()])));
        // A truncated reading did not read the oil chunk in full: the deposit stays a destination.
        var truncated = memory.MergeCharted(ChartedResourceSnapshot.Parse(Reply(300, [], ["crude-oil"], truncated: true, completeRadius: 40)));
        Assert.Contains(truncated.Resources, r => r.Name == "crude-oil");
        var complete = truncated.MergeCharted(ChartedResourceSnapshot.Parse(Reply(400, [], ["crude-oil"])));
        Assert.DoesNotContain(complete.Resources, r => r.Name == "crude-oil");
        Assert.Contains(complete.Resources, r => r.Name == "iron-ore" && r.Origin == ResourceSighting.Charted);
        Assert.Contains(complete.Resources, r => r.Name == "copper-ore" && r.Origin == ResourceSighting.Local);
        // A map reading never removes a local sighting: only a local view does.
        var copper = complete.MergeCharted(ChartedResourceSnapshot.Parse(Reply(500, [], ["copper-ore"])));
        Assert.Contains(copper.Resources, r => r.Name == "copper-ore" && r.Origin == ResourceSighting.Local);
    }

    [Fact]
    public void ALocalViewDecidesOverAChartedDestinationInsideItsBounds()
    {
        var map = OilMap();
        var memory = ResourceMemorySnapshot.Empty(map).MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil()], ["crude-oil"])));
        var view = map with { CollectedTick = 300, Bounds = new(new(60, 0), new(100, 40)), Actor = map.Actor with { Position = new(80, 20) } };
        Assert.Empty(memory.Merge(view with { Entities = [] }).Resources);
        var seen = memory.Merge(view with { Entities = [new("1:crude-oil:80.5:24.5", "crude-oil", new(80.5, 24.5),
            new(new(79.1, 23.1), new(81.9, 25.9)), 0, "neutral", Amount: 100000)] });
        Assert.Equal(ResourceSighting.Local, Assert.Single(seen.Resources).Origin);
        // In the same cell at the same tick, the local view outranks the reading.
        var sameTick = ResourceMemorySnapshot.Empty(map).Merge(map with { Entities = seen.Resources.Select(r => new SpatialEntity(r.EntityId, r.Name,
            r.Position, new(new(79.1, 23.1), new(81.9, 25.9)), 0, "neutral", Amount: 1)).ToArray(), Bounds = view.Bounds, Actor = view.Actor,
            CollectedTick = 200 });
        var reread = sameTick.MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil() with { Sample = new("other", new(81.5, 25.5)) }], ["crude-oil"])));
        Assert.Equal(ResourceSighting.Local, Assert.Single(reread.Resources).Origin);
    }

    [Fact]
    public void AVersionOneMemoryReadsAsLocalSightingsAndIsRewrittenWithOrigins()
    {
        const string old = """
            {"worldId":"world","surfaceIndex":1,"lastTick":100,"resources":[{"entityId":"ore","name":"copper-ore","position":{"x":4,"y":4},"observedTick":100}],
             "surveyedCells":[],"truncated":false,"version":1}
            """;
        var memory = JsonSerializer.Deserialize<ResourceMemorySnapshot>(old, Protocol.Json)!;
        memory.ValidateFor("world", 1, 100);
        Assert.Equal((1, ResourceSighting.Local), (memory.Version, memory.Resources[0].Origin));
        var upgraded = memory.MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil()], ["crude-oil"])));
        Assert.Equal(ResourceMemorySnapshot.CurrentVersion, upgraded.Version);
        Assert.Contains("\"origin\":\"charted\"", JsonSerializer.Serialize(upgraded, Protocol.Json));
        Assert.Throws<InvalidDataException>(() => (upgraded with { Resources = [upgraded.Resources[0] with { Origin = "rumour" }] }).ValidateFor("world", 1, 200));
    }

    [Fact]
    public void AReadingOfAnotherWorldSurfaceOrAnOlderTickIsRefused()
    {
        var map = ResourceMemoryTests.Map();
        var memory = ResourceMemorySnapshot.Empty(map).Merge(map);
        var reading = ChartedResourceSnapshot.Parse(Reply(200, [Oil()], ["crude-oil"]));
        Assert.Throws<InvalidDataException>(() => memory.MergeCharted(reading with { CollectedTick = 99 }));
        Assert.Throws<InvalidDataException>(() => memory.MergeCharted(reading with { SurfaceIndex = 2 }));
        Assert.Throws<InvalidDataException>(() => memory.MergeCharted(reading with { Scope = reading.Scope with { WorldId = "other" } }));
    }

    [Fact]
    public async Task TheSessionClientRecordsEveryReadingAsChartedDestinations()
    {
        var session = new RuntimeSession(directory, "factorio.exe", "config.ini", "mods", "save.zip", 0, 1, 2, "fixture-only", "session", "world", 1, true);
        var game = new ChartedGame { Reading = Reply(200, [Oil(), Iron()]) };
        var client = new SessionGameClient(session, game);
        await new ChartedResourceClient(client).CaptureAsync(["crude-oil", "iron-ore"]);
        var later = ResourceMemoryTests.Map() with { CollectedTick = 300, Entities = [] };
        var memory = await client.ReadResourceMemoryAsync(later);
        Assert.All(memory.Resources, r => Assert.Equal(ResourceSighting.Charted, r.Origin));
        Assert.Equal(["crude-oil", "iron-ore"], memory.Resources.Select(r => r.Name).Order());
        Assert.Empty(memory.SurveyedCells);
    }

    [Fact]
    public void TheNearestRememberedDepositIsTheDestinationWhateverItsOrigin()
    {
        var map = OilMap();
        var far = new SpatialEntity("far-oil", "crude-oil", new(300.5, 0.5), new(new(299.1, -0.9), new(301.9, 1.9)), 0, "neutral", Amount: 1000);
        var local = ResourceMemorySnapshot.Empty(map).Merge(map with { Bounds = new(new(250, -40), new(340, 40)), Entities = [far],
            Actor = map.Actor with { Position = new(300, 0) } });
        var memory = local.MergeCharted(ChartedResourceSnapshot.Parse(Reply(200, [Oil(), Iron()])));
        var origin = new MapPosition(0, 0);
        Assert.Equal("1:crude-oil:80.5:24.5", memory.NearestOf("crude-oil", origin)!.EntityId);
        // A deposit refused locally or in a death zone is skipped for the next nearest one.
        Assert.Equal("far-oil", memory.NearestOf("crude-oil", origin, r => r.EntityId != "1:crude-oil:80.5:24.5")!.EntityId);
        Assert.Null(memory.NearestOf("crude-oil", origin, _ => false));
        // General searches by product follow charted deposits through the same memory.
        var catalog = Catalog(map.Scope);
        Assert.Equal(ResourceSighting.Charted, memory.Nearest("iron-ore", catalog, origin)!.Origin);
    }

    [Fact]
    public async Task TheMapIsReadOnTheFirstExplorationStepAndEveryEighthAfter()
    {
        var game = new ChartedGame { Reading = Reply(200, [Oil()], ["crude-oil"]) };
        var journal = new MemoryJournal();
        var survey = new ChartedResourceSurvey(game, journal);
        int read = 0;
        for (int step = 0; step < 17; step++)
            if (await survey.BeforeExplorationAsync(["crude-oil"], "test", CancellationToken.None) is not null) read++;
        Assert.Equal(3, read);
        Assert.Equal(3, journal.Entries.Count(e => e.Type == "charted-resource-survey"));
        Assert.Null(await survey.BeforeExplorationAsync([], "test", CancellationToken.None));
    }

    [Fact]
    public async Task AWorldWithoutTheReadingKeepsItsSearchButOtherErrorsStopIt()
    {
        var journal = new MemoryJournal();
        var stale = new ChartedGame { Reading = new GameResponse(1, "r", false, 200, default, new("unknown_action", "Unsupported RPC action")) };
        Assert.Null(await new ChartedResourceSurvey(stale, journal).ReadAsync(["crude-oil"], "test", CancellationToken.None));
        Assert.Equal("charted-resource-survey-unavailable", Assert.Single(journal.Entries).Type);
        var dead = new ChartedGame { Reading = new GameResponse(1, "r", false, 200, default, new("actor_dead", "Wait for the character to respawn")) };
        await Assert.ThrowsAsync<GameRpcException>(() => new ChartedResourceSurvey(dead, journal).ReadAsync(["crude-oil"], "test", CancellationToken.None));
    }

    [Fact]
    public void OnlyNativeResourcesYieldingTheProductAreRead()
    {
        var catalog = Catalog(new("world", "session", "actor", 1, 2));
        Assert.Equal(["iron-ore"], ChartedResourceSurvey.Sources(catalog, "iron-ore"));
        Assert.Equal(["crude-oil"], ChartedResourceSurvey.Sources(catalog, "crude-oil"));
        Assert.Empty(ChartedResourceSurvey.Sources(catalog, "wood"));
        Assert.Empty(ChartedResourceSurvey.Sources(catalog with { MiningSourceTypes = null }, "iron-ore"));
    }

    [Fact]
    public void ARadarIsPlacedInsideTheSupplyAreaOfAPole()
    {
        var map = SteamPowerPlannerTests.Map(true);
        var radar = new EntityGeometry("radar", "radar", new(new(-1.4, -1.4), new(1.4, 1.4)), map.Prototypes["boiler"].Mask, 3, 3, IsElectric: true);
        var pole = new SpatialEntity("p", "pole", new(5.5, 5.5), new(new(5.35, 5.35), new(5.65, 5.65)), 0, "own", Power: new(0, 7));
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["radar"] = radar },
            Entities = [pole], Items = new Dictionary<string, PlaceableItem>(map.Items) { ["radar"] = new("radar", 50) } };
        var placement = new PoweredMachinePlanner().Place(map, "radar", pole);
        Assert.NotNull(placement);
        double supply = map.Prototypes["pole"].SupplyArea!.Value;
        Assert.True(new WorldBox(new(5.5 - supply, 5.5 - supply), new(5.5 + supply, 5.5 + supply)).Overlaps(radar.CollisionBox.Translate(placement.Position)));
    }

    [Fact]
    public void MaintenanceRebuildsADestroyedRadarAtItsPlan()
    {
        var plan = new PlannedEntity("machine", "radar", new(-10.5, 3.5), 0);
        var cell = new FactoryCell("radar-1", 0, new(0, 0, true), RadarController.Kind, "radar", null,
            new Dictionary<string, string> { ["machine"] = "77" }, "ready", 100, Plan: new Dictionary<string, PlannedEntity> { ["machine"] = plan });
        var state = new FactoryState(1, "world", [], [cell]);
        var missing = Assert.Single(FactoryMaintenance.Missing(state, new HashSet<string>()));
        Assert.Equal(("radar-1", "machine", plan), (missing.Cell.Id, missing.Role, missing.Plan));
        Assert.Empty(FactoryMaintenance.Missing(state, new HashSet<string> { "77" }));
    }

    [Fact]
    public async Task TheQualificationJudgesTheSearchFromItsJournal()
    {
        string path = Path.Combine(directory, "journal.jsonl");
        var journal = new ControllerJournal(path);
        await journal.AppendAsync("charted-resource-survey", new { purpose = "resource-research", collectedTick = 200L,
            nearest = new[] { Oil() } }, CancellationToken.None);
        await journal.AppendAsync("resource-research-search", new { resourceName = "crude-oil",
            historical = new ResourceSighting("1:crude-oil:80.5:24.5", "crude-oil", new(80.5, 24.5), 200, ResourceSighting.Charted),
            frontier = new ExplorationWaypoint(new(20, 4), 210), deferredObservedResources = Array.Empty<string>() }, CancellationToken.None);
        await journal.AppendAsync("submission", new { kind = "build", args = new { item = "pumpjack" } }, CancellationToken.None);
        var facts = await ChartedResourceQualification.JournalAsync(path, new(80.5, 24.5), CancellationToken.None);
        Assert.Equal((1, 0, ResourceSighting.Charted, 210L, 200L, "resource-research", 1, 0), (facts.Searches, facts.BlindSearches,
            facts.FirstOrigin, facts.FirstSearchTick, facts.SurveyTickOfDeposit, facts.SurveyPurposeOfDeposit, facts.PumpBuilds, facts.Mines));
        await journal.AppendAsync("resource-research-search", new { resourceName = "crude-oil", historical = (object?)null,
            frontier = new ExplorationWaypoint(new(30, 4), 220) }, CancellationToken.None);
        Assert.Equal(1, (await ChartedResourceQualification.JournalAsync(path, new(80.5, 24.5), CancellationToken.None)).BlindSearches);
    }

    internal static ChartedDeposit Oil() => new("crude-oil", new(2, 0), false, new("1:crude-oil:80.5:24.5", new(80.5, 24.5)), 1, 100000);
    private static ChartedDeposit Iron() => new("iron-ore", new(-3, -3), true, new("1:iron-ore:-76.5:-64.5", new(-76.5, -64.5)), 473, 328019);

    private static object Data(long tick, IReadOnlyList<ChartedDeposit> deposits, string[]? names = null, bool truncated = false,
        double? completeRadius = null, int radius = 512) => new
    {
        scope = new ActorScope("world", "session", "actor", 1, 2),
        collectedTick = tick,
        surfaceIndex = 1,
        center = new MapPosition(0, 0),
        radius,
        names = names ?? ["crude-oil", "iron-ore"],
        limit = 64,
        deposits,
        coverage = new
        {
            visibility = ChartedResourceSnapshot.MapVisibility,
            consideredChunks = 900,
            chartedChunks = deposits.Where(d => d.Charted).Select(d => d.Chunk).Distinct().Count() + 1,
            requestedChunks = deposits.Where(d => !d.Charted).Select(d => d.Chunk).Distinct().Count() + 1,
            entities = deposits.Sum(d => d.Count),
            truncated,
            completeRadius = completeRadius ?? radius
        }
    };

    internal static GameResponse Reply(long tick, IReadOnlyList<ChartedDeposit> deposits, string[]? names = null, bool truncated = false,
        double? completeRadius = null) => new(1, "r", true, tick, deposits.Count == 0
            ? EmptyLuaDeposits(Data(tick, deposits, names, truncated, completeRadius))
            : Protocol.ToElement(Data(tick, deposits, names, truncated, completeRadius)));

    // The mod serializes an empty Lua list as an empty object.
    private static JsonElement EmptyLuaDeposits(object data)
    {
        var node = JsonSerializer.SerializeToNode(data, Protocol.Json)!.AsObject();
        node["deposits"] = new JsonObject();
        return JsonSerializer.SerializeToElement(node, Protocol.Json);
    }

    private static SpatialSnapshot OilMap()
    {
        var map = ResourceMemoryTests.Map();
        var prototypes = map.Prototypes.ToDictionary();
        prototypes["crude-oil"] = new("crude-oil", "resource", new(new(-1.4, -1.4), new(1.4, 1.4)), new([], false, false, false), 1, 1);
        return map with { Prototypes = prototypes, Entities = [] };
    }

    private static ProductionCatalog Catalog(ActorScope scope) => new(scope, 100, [], new Dictionary<string, NativeItem>(),
        new Dictionary<string, NativeMaterial[]>
        {
            ["iron-ore"] = [new("iron-ore", "item", 1)],
            ["crude-oil"] = [new("crude-oil", "fluid", 10)],
            ["tree-01"] = [new("wood", "item", 4)]
        }, new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>(),
        MiningSourceTypes: new Dictionary<string, string> { ["iron-ore"] = "resource", ["crude-oil"] = "resource", ["tree-01"] = "tree" });

    private sealed class ChartedGame : IGameClient
    {
        public GameResponse Reading { get; init; } = null!;
        public List<GameRequest> Requests { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Reading with { RequestId = request.RequestId });
        }
    }

    private sealed class MemoryJournal : IControllerJournal
    {
        public List<(string Type, object Data)> Entries { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Entries.Add((type, data));
            return Task.CompletedTask;
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
