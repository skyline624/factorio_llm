using System.Text.Json;
using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

[Collection("Retreat progress")]
public sealed class PortableDefenseControllerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "portable-defense-" + Guid.NewGuid().ToString("N"));
    public PortableDefenseControllerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task OneArbiterBuildsLoadsAndVerifiesTwoCoversBeforeRepositioningTheHealthyActor()
    {
        var game = new Game(); var journal = new Journal();
        var registry = new FactoryRegistry(directory);
        var defense = new DefenseController(game, journal, new ReflexEventLog(), registry);
        Assert.Equal("defending", (await defense.StepAsync()).State);
        Assert.Equal("build", Assert.Single(game.Submissions).Kind);
        var cell = Assert.Single((await registry.LoadAsync("world", default)).Cells);
        Assert.Equal(("turret", "ready", "native-turret", "paid-turret"), (cell.Kind, cell.Status, cell.MachineItem, cell.Entities["turret"]));
        Assert.Equal(game.TurretPosition, cell.Plan!["turret"].Position);
        Assert.Equal(1, game.Stock["native-turret"]);
        Assert.Equal("defending", (await defense.StepAsync()).State);
        Assert.Equal(["build", "insert"], game.Submissions.Select(s => s.Kind));
        Assert.Equal(40, game.Stock["basic-rounds"]);
        Assert.Equal(200, game.LoadedRounds);
        Assert.DoesNotContain(journal.Events, e => e.Type == "portable-defense-coverage");
        await defense.StepAsync();
        var coverage = Assert.Single(journal.Events, e => e.Type == "portable-defense-coverage").Data;
        Assert.True(coverage.GetProperty("ready").GetBoolean());
        Assert.Equal(["build", "insert", "build"], game.Submissions.Select(s => s.Kind));
        await defense.StepAsync();
        Assert.Equal(20, game.Stock["basic-rounds"]);
        Assert.Equal(0, game.Stock["native-turret"]);
        Assert.Equal(400, game.LoadedRounds);
        Assert.Equal(2, (await registry.LoadAsync("world", default)).Cells.Count);
        await defense.StepAsync();
        Assert.Equal(["build", "insert", "build", "insert", "move"], game.Submissions.Select(s => s.Kind));
        Assert.Equal(2, journal.Events.Count(e => e.Type == "portable-defense-coverage"));
        Assert.DoesNotContain("cancel", game.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AKnownUnpaidRefusalSelectsADifferentFreshPlacementEvenAfterALostResponse(bool lost)
    {
        var game = new Game { BlockedBuilds = 1, LostKind = lost ? "build" : null };
        var registry = new FactoryRegistry(directory);
        var defense = new DefenseController(game, new Journal(), registry: registry);
        if (lost)
        {
            var error = await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => defense.StepAsync());
            Assert.Equal("reconciled", (await defense.StepAsync()).State);
            Assert.Equal(error.OperationId, Assert.Single(game.Queries));
        }
        else await defense.StepAsync();
        Assert.Equal(2, game.Stock["native-turret"]);
        Assert.Equal(60, game.Stock["basic-rounds"]);
        Assert.Empty((await registry.LoadAsync("world", default)).Cells);
        await defense.StepAsync();
        Assert.Equal(["build", "build"], game.Submissions.Select(s => s.Kind));
        Assert.NotEqual(game.Submissions[0].OperationId, game.Submissions[1].OperationId);
        var first = game.Submissions[0].Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        var replacement = game.Submissions[1].Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        Assert.True(first.DistanceTo(replacement) >= 1.5);
        Assert.Equal(2, game.Calls.Count(c => c == "spatial"));
        Assert.Equal(1, game.Stock["native-turret"]);
        Assert.Single((await registry.LoadAsync("world", default)).Cells);
    }

    [Fact]
    public async Task RepeatedKnownPlacementRefusalsAreBoundedAndLeaveTheGunAvailable()
    {
        var game = new Game { BlockedBuilds = 10 }; var journal = new Journal();
        var defense = new DefenseController(game, journal);
        for (int i = 0; i < 4; i++) await defense.StepAsync();
        Assert.Equal(["build", "build", "build", "shoot"], game.Submissions.Select(s => s.Kind));
        Assert.Equal(3, game.Submissions.Where(s => s.Kind == "build").Select(s => s.Args.GetProperty("position").GetRawText()).Distinct().Count());
        Assert.Equal(3, journal.Events.Count(e => e.Type == "portable-defense-refused"));
        Assert.Equal(2, game.Stock["native-turret"]);
        Assert.Equal(60, game.Stock["basic-rounds"]);
    }

    [Fact]
    public async Task AFailedPlacementWithAnInventoryMutationCannotStartAnAlternative()
    {
        var game = new Game { BlockedBuilds = 1, RefusalEvidence = "mutation" };
        var defense = new DefenseController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => defense.StepAsync());
        Assert.Single(game.Submissions);
        Assert.DoesNotContain(game.Submissions, s => s.Kind == "shoot");
    }

    [Fact]
    public async Task ARefusalWithoutNativeEmptyInventoryEvidenceDoesNotAuthorizeAnotherPlacement()
    {
        var game = new Game { BlockedBuilds = 1, RefusalEvidence = "missing" };
        var defense = new DefenseController(game, new Journal());
        await defense.StepAsync();
        await defense.StepAsync();
        Assert.Equal(["build", "shoot"], game.Submissions.Select(s => s.Kind));
        Assert.Equal(2, game.Stock["native-turret"]);
    }

    [Fact]
    public async Task AChangedNativeCaptureAfterRefusalCannotUseThePreviousReplacementGeometry()
    {
        var game = new Game { BlockedBuilds = 1 };
        var defense = new DefenseController(game, new Journal());
        await defense.StepAsync();
        game.ChangedMap = "moved";
        Assert.Equal("defending", (await defense.StepAsync()).State);
        Assert.Single(game.Submissions);
        Assert.Equal(2, game.Stock["native-turret"]);
    }

    [Theory]
    [InlineData(true, "unit", false)]
    [InlineData(true, null, false)]
    [InlineData(false, "turret", false)]
    [InlineData(true, "turret", true)]
    public async Task RetreatKeepsFullSpeedForMobileOrUnknownThreatsAndOnlyFiresAtKnownStaticTargets(
        bool supported, string? type, bool fires)
    {
        var game = new Game { SupportsMovingFire = supported, EnemyType = type, Health = 100 };
        game.Stock["native-turret"] = 0;
        await new DefenseController(game, new Journal()).StepAsync();
        var movement = Assert.Single(game.Submissions);
        Assert.Equal("move", movement.Kind);
        Assert.Equal(fires, movement.Args.TryGetProperty("shootEntityId", out var target));
        if (fires) Assert.Equal("biter-0", target.GetString());
        Assert.Equal(60, game.Stock["basic-rounds"]);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("insert")]
    public async Task LostMutationResponseIsQueriedByItsIdentityWithoutRepeatingTheMutation(string lost)
    {
        var game = new Game { LostKind = lost };
        var defense = new DefenseController(game, new Journal(), registry: new FactoryRegistry(directory));
        if (lost == "insert") await defense.StepAsync();
        var error = await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => defense.StepAsync());
        Assert.Equal("reconciled", (await defense.StepAsync()).State);
        Assert.Equal(error.OperationId, Assert.Single(game.Queries));
        await defense.StepAsync();
        Assert.Single(game.Submissions, s => s.OperationId == error.OperationId);
        int builds = game.Submissions.Count(s => s.Kind == "build");
        Assert.Equal(builds, (await new FactoryRegistry(directory).LoadAsync("world", default)).Cells.Count);
        Assert.Equal(2 - builds, game.Stock["native-turret"]);
        Assert.Equal(40, game.Stock["basic-rounds"]);
    }

    [Fact]
    public async Task AnExistingWorkActionIsStoppedBeforeEmergencyConstruction()
    {
        var game = new Game { Active = Receipt("production", "move", "running", new { }) };
        var defense = new DefenseController(game, new Journal());
        Assert.Equal("preempted", (await defense.StepAsync()).State);
        Assert.Empty(game.Submissions);
        Assert.Equal(2, game.Stock["native-turret"]);
        await defense.StepAsync();
        Assert.Equal("build", Assert.Single(game.Submissions).Kind);
        Assert.Single(game.Calls, c => c == "cancel");
    }

    [Fact]
    public async Task AnOwnedBuildIsNeverCancelledByItsOwnPackResponseAndItsFinalReceiptRegistersTheCell()
    {
        var game = new Game { RunningBuild = true };
        var registry = new FactoryRegistry(directory);
        var defense = new DefenseController(game, new Journal(), registry: registry);
        await defense.StepAsync();
        await defense.StepAsync();
        Assert.Single(game.Submissions);
        Assert.DoesNotContain("cancel", game.Calls);
        Assert.Empty((await registry.LoadAsync("world", default)).Cells);
        await defense.StopOwnedActionAsync(default);
        Assert.Single((await registry.LoadAsync("world", default)).Cells);
        Assert.DoesNotContain("cancel", game.Calls);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("moved")]
    [InlineData("stale")]
    public async Task ChangedTerrainCannotSubmitTheOldPlacementOrAnOldShoot(string changed)
    {
        var game = new Game { ChangedMap = changed };
        var defense = new DefenseController(game, new Journal());
        if (changed == "scope") await Assert.ThrowsAsync<InvalidDataException>(() => defense.StepAsync());
        else Assert.Equal("defending", (await defense.StepAsync()).State);
        Assert.Empty(game.Submissions);
        Assert.Equal(2, game.Stock["native-turret"]);
    }

    [Theory]
    [InlineData("build-id")]
    [InlineData("build-cost")]
    [InlineData("build-position")]
    [InlineData("build-name")]
    [InlineData("insert-target")]
    [InlineData("insert-cost")]
    [InlineData("insert-requested")]
    public async Task InconsistentNativeMutationEvidenceStopsTheSequence(string wrong)
    {
        var game = new Game { WrongReceipt = wrong };
        var registry = new FactoryRegistry(directory);
        var defense = new DefenseController(game, new Journal(), registry: registry);
        if (wrong.StartsWith("insert", StringComparison.Ordinal)) await defense.StepAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => defense.StepAsync());
        Assert.DoesNotContain(game.Submissions, s => s.Kind == "shoot");
        if (wrong.StartsWith("build", StringComparison.Ordinal)) Assert.Empty((await registry.LoadAsync("world", default)).Cells);
    }

    [Fact]
    public async Task LosingTheTurretBetweenBuildAndLoadDoesNotRepeatConstructionOrClaimCoverage()
    {
        var game = new Game(); var journal = new Journal();
        var defense = new DefenseController(game, journal);
        await defense.StepAsync();
        game.HideTurret = true;
        await defense.StepAsync();
        Assert.Equal(["build", "shoot"], game.Submissions.Select(s => s.Kind));
        Assert.Contains(journal.Events, e => e.Type == "portable-defense-load-deferred");
        Assert.DoesNotContain(journal.Events, e => e.Type == "portable-defense-coverage");
    }

    [Fact]
    public async Task ASavedProductionSnapshotCannotEraseAReflexTurret()
    {
        var registry = new FactoryRegistry(directory);
        var before = await registry.LoadAsync("world", default);
        await new DefenseController(new Game(), new Journal(), registry: registry).StepAsync();
        await registry.SaveAsync(before with { Targets = new Dictionary<string, double> { ["chemical-science-pack"] = 30 } }, default);
        var saved = await registry.LoadAsync("world", default);
        Assert.Single(saved.Cells);
        Assert.Equal(30, saved.Targets!["chemical-science-pack"]);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("no-inventory")]
    [InlineData("no-turret")]
    [InlineData("short-ammo")]
    public async Task UnsupportedOrUnpaidEmergencyConstructionIsNotSubmitted(string invalid)
    {
        var game = new Game { Invalid = invalid };
        if (invalid == "no-turret") game.Stock["native-turret"] = 0;
        if (invalid == "short-ammo") game.Stock["basic-rounds"] = 39;
        await new DefenseController(game, new Journal()).StepAsync();
        Assert.DoesNotContain(game.Submissions, s => s.Kind is "build" or "insert");
        if (invalid == "manual") Assert.Empty(game.Submissions);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("fraction")]
    [InlineData("null")]
    [InlineData("duplicate")]
    public void InvalidNativeCarriedStockCannotBecomeATurretBudget(string invalid)
    {
        var game = new Game();
        var node = JsonSerializer.SerializeToNode(game.Observe(), Protocol.Json)!;
        node["agent"]!["inventory"] = invalid switch
        {
            "negative" => JsonNode.Parse("{\"native-turret\":-1}"),
            "fraction" => JsonNode.Parse("{\"native-turret\":1.5}"),
            "duplicate" => null,
            _ => null
        };
        string json = node.ToJsonString();
        if (invalid == "duplicate") json = json.Replace("\"inventory\":null", "\"inventory\":{\"native-turret\":1,\"native-turret\":2}", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => SafetyObservation.Parse(new(1, "bad-stock", true, 100, document.RootElement.Clone())));
    }

    private static JsonElement Receipt(string id, string kind, string status, object effects, object? error = null) => Protocol.ToElement(new
        { operationId = id, kind, status, acceptedTick = 100, updatedTick = 100, effects, error });

    private sealed class Journal : IControllerJournal
    {
        public List<(string Type, JsonElement Data)> Events { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        { Events.Add((type, Protocol.ToElement(data))); return Task.CompletedTask; }
    }

    private sealed class Game : IGameClient
    {
        private readonly SpatialSnapshot terrain;
        private readonly ProductionCatalog catalog;
        public Dictionary<string, long> Stock { get; }
        public Game() { var fixture = PortableDefenseTests.Fixture(); terrain = fixture.Map; catalog = fixture.Catalog; Stock = fixture.Carried; }
        public string? LostKind { get; init; }
        public string? ChangedMap { get; set; }
        public string? WrongReceipt { get; init; }
        public string? Invalid { get; init; }
        public bool RunningBuild { get; init; }
        public bool HideTurret { get; set; }
        public int BlockedBuilds { get; set; }
        public string RefusalEvidence { get; init; } = "empty";
        public bool SupportsMovingFire { get; init; }
        public string? EnemyType { get; init; } = "unit";
        public double Health { get; init; } = 250;
        private readonly List<(string Id, MapPosition Position, long Rounds)> turrets = [];
        private bool responseLost;
        public long LoadedRounds => turrets.Sum(t => t.Rounds);
        public MapPosition? TurretPosition => turrets.Count > 0 ? turrets[0].Position : null;
        public JsonElement? Active { get; set; }
        private JsonElement? confirmed;
        public List<OperationSubmission> Submissions { get; } = [];
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "observe" => Observe(), "spatial" => Map(), "production_catalog" => catalog,
                "submit" => Submit(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                "operation" => Query(request.Arguments.GetProperty("operationId").GetString()!),
                "cancel" => Cancel(request.Arguments.GetProperty("operationId").GetString()!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true,
                request.Action == "spatial" ? Map().CollectedTick : 100, Protocol.ToElement(data)));
        }

        public object Observe()
        {
            var agent = new Dictionary<string, object> { ["alive"] = true, ["controlMode"] = Invalid == "manual" ? "manual" : "ai",
                ["stopUnconfirmed"] = false, ["position"] = new MapPosition(0, 0), ["health"] = Health, ["maxHealth"] = 250,
                ["weapon"] = new { ready = true, rounds = 200, range = 18 } };
            if (Invalid != "no-inventory") agent["inventory"] = Stock;
            var frame = new Dictionary<string, object> { ["scope"] = terrain.Scope, ["collectedTick"] = 100L,
                ["coverage"] = new { atomic = true, collectionStartTick = 100L, collectionEndTick = 100L,
                    enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemiesTruncated = false,
                    movementFire = SupportsMovingFire ? "native-walking-and-shooting" : null },
                ["agent"] = agent, ["enemies"] = Enumerable.Range(0, 3).Select(i => new
                    { id = "biter-" + i, type = EnemyType, position = new MapPosition(12 + i, i), collectedTick = 100L }).ToArray(),
                ["defenses"] = turrets.Where(t => t.Rounds > 0).Select(t => new DefensiveRefuge(t.Id, t.Position, 18, t.Rounds, 100)).ToArray() };
            if (Active is { } active) frame["operation"] = active;
            return frame;
        }

        private SpatialSnapshot Map()
        {
            var map = terrain;
            if (!HideTurret)
                map = map with { Entities = turrets.Select(t => new SpatialEntity(t.Id, "turret-entity", t.Position,
                    new(new(t.Position.X - .7, t.Position.Y - .7), new(t.Position.X + .7, t.Position.Y + .7)), 0, "agent")).ToArray() };
            return ChangedMap switch
            {
                "scope" => map with { Scope = map.Scope with { Incarnation = 2 } },
                "moved" => map with { Actor = map.Actor with { Position = new(2, 0) } },
                "stale" => map with { CollectedTick = 161 }, _ => map
            };
        }

        private object Submit(OperationSubmission submission)
        {
            Submissions.Add(submission);
            if (submission.Kind == "build" && BlockedBuilds > 0)
            {
                BlockedBuilds--;
                object refusal = RefusalEvidence switch
                {
                    "mutation" => new { inventoryDelta = new Dictionary<string, long> { ["native-turret"] = -1 } },
                    "missing" => new { },
                    _ => new { inventoryDelta = new Dictionary<string, long>() }
                };
                confirmed = Receipt(submission.OperationId, "build", "failed", refusal,
                    new { code = "placement_blocked", message = "Native placement changed after capture." });
                Active = confirmed;
                if (LostKind == "build" && !responseLost) { responseLost = true; throw new IOException("Known refusal response lost."); }
                return Active.Value;
            }
            JsonNode effect;
            if (submission.Kind == "build")
            {
                Assert.Equal(terrain.Scope, submission.Scope);
                Stock["native-turret"]--;
                var position = submission.Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                string id = turrets.Count == 0 ? "paid-turret" : "paid-turret-" + (turrets.Count + 1);
                turrets.Add((id, position, 0));
                effect = JsonSerializer.SerializeToNode(new { entityId = id, entityName = "turret-entity",
                    entityPosition = position, consumed = new Dictionary<string, long> { ["native-turret"] = 1 } }, Protocol.Json)!;
                if (WrongReceipt == "build-id") effect["entityId"] = "";
                if (WrongReceipt == "build-cost") effect["consumed"]!["native-turret"] = 0;
                if (WrongReceipt == "build-name") effect["entityName"] = "enemy-turret";
                if (WrongReceipt == "build-position") effect["entityPosition"]!["x"] = 50;
            }
            else if (submission.Kind == "insert")
            {
                string id = submission.Args.GetProperty("entityId").GetString()!;
                int index = turrets.FindIndex(t => t.Id == id);
                Assert.True(index >= 0);
                Assert.Equal(("ammo", "basic-rounds", 20), (submission.Args.GetProperty("inventory").GetString(),
                    submission.Args.GetProperty("item").GetString(), submission.Args.GetProperty("count").GetInt32()));
                Stock["basic-rounds"] -= 20; turrets[index] = turrets[index] with { Rounds = 200 };
                effect = JsonSerializer.SerializeToNode(new { targetId = id, inventory = "ammo", item = "basic-rounds",
                    direction = "from_actor", requested = 20, transferred = 20 }, Protocol.Json)!;
                if (WrongReceipt == "insert-target") effect["targetId"] = "unrelated-turret";
                if (WrongReceipt == "insert-cost") effect["transferred"] = 0;
                if (WrongReceipt == "insert-requested") effect["requested"] = 200;
            }
            else effect = new JsonObject();
            confirmed = Receipt(submission.OperationId, submission.Kind, submission.Kind is "shoot" or "move" ? "running" : "completed", effect);
            Active = RunningBuild && submission.Kind == "build" ? Receipt(submission.OperationId, submission.Kind, "running", new { }) : confirmed;
            if (LostKind == submission.Kind && !responseLost) { responseLost = true; throw new IOException("Native mutation response lost after payment."); }
            return Active.Value;
        }

        private object Query(string id) { Queries.Add(id); Assert.Equal(confirmed!.Value.GetProperty("operationId").GetString(), id); return confirmed.Value; }
        private object Cancel(string id) { Active = Receipt(id, Active!.Value.GetProperty("kind").GetString()!, "cancelled", new { }); return Active.Value; }
    }

    public void Dispose() => Directory.Delete(directory, true);
}
