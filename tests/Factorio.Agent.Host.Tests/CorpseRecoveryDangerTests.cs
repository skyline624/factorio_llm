using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

/// <summary>Campaign 2026-10-01 (seed 20261002): every recovery walked back to the fresh corpse and died near it.</summary>
public sealed class CorpseRecoveryDangerTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 2, 4);
    private static readonly NativeDeathTransition Death = new(1, 49000, 17, 1, new(20, 0));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnemyBesideTheActorIsDefendedBeforeReadingDeathZonesOrSelectingACorpse(bool lostReply)
    {
        // Seed 20261072: recovery observed enemies one tile away but began its inventory reads before defending.
        var game = new Game { Tick = Death.DeathTick + DangerZones.LifetimeTicks, AllowDefense = true,
            LoseDefenseReply = lostReply, Enemies = [("unit", new(1, 0)), ("unit-spawner", new(24, 0))] };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);

        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Empty(result.Collected);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Equal(1, game.Submissions);
        Assert.Equal([1], game.SubmissionsAtDeathReads);
        Assert.Equal(["observe", "submit"], game.Calls.Take(2));
        Assert.DoesNotContain("production_catalog", game.Calls);
        Assert.All(game.ObservedRadii, radius => Assert.Equal(CorpseRecoveryController.InspectionRadius, radius));
        Assert.NotEmpty(journal.All("corpse-recovery-defense"));
        if (lostReply) Assert.Contains("operation", game.Calls);
    }

    [Fact]
    public async Task AChangedIncarnationAfterInitialDefenseCannotReceiveAnotherRecoveryIntent()
    {
        var game = new Game { AllowDefense = true, ChangeScopeAfterDefense = true, Enemies = [("unit", new(1, 0))] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new CorpseRecoveryController(game, new Journal()).RunAsync(Death, Scope, default));
        Assert.Equal(1, game.Submissions);
        Assert.Empty(game.SubmissionsAtDeathReads);
        Assert.DoesNotContain("production_catalog", game.Calls);
    }

    [Fact]
    public async Task ADeferredRecoveryStillProtectsTheActorWithoutCollectingTheUnsafeBody()
    {
        var game = new Game { AllowDefense = true, Enemies = [("unit", new(1, 0))] };
        var result = await CorpseRecoveryController.DeferAsync(game, new Journal(), Death, Scope, 49000, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Null(result.RetryTick);
        Assert.Empty(result.Collected);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Equal(1, game.Submissions);
        Assert.Equal(["observe", "submit", "observe"], game.Calls);
        Assert.All(game.ObservedRadii, radius => Assert.Equal(32, radius));
        Assert.Empty(game.SubmissionsAtDeathReads);
    }

    [Fact]
    public async Task FreshDeathZoneWithAVisibleBiterDefersWithoutApproaching()
    {
        var game = new Game { Enemies = [("unit", new(24, 0))] };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(Death.DeathTick + DangerZones.LifetimeTicks, result.RetryTick);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Equal(["observe", "observe"], game.Calls);
        Assert.Equal("occupied", journal.Single("danger-zone-inspection").GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData(null)]       // No visible enemy at all.
    [InlineData("turret")]   // A worm: routing keeps its range, as before.
    [InlineData("far-unit")] // A visible biter outside the zone.
    public async Task ZoneSeenFreeOfMobileEnemiesAllowsTheApproach(string? enemy)
    {
        var game = new Game
        {
            Enemies = enemy switch { null => [], "turret" => [("turret", new(24, 0))], _ => [("unit", new(-40, 0))] }
        };
        var journal = new Journal();
        var travel = await Assert.ThrowsAsync<InvalidOperationException>(() => new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default));
        Assert.Contains("production_catalog", travel.Message);
        Assert.Equal("clear", journal.Single("danger-zone-inspection").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task ZoneBeyondNormalSightDefersUntilItExpires()
    {
        // The actor does not walk closer to look: the zone is 60 tiles away and its far side beyond the 64-tile view.
        var game = new Game { Actor = new(-40, 0) };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(Death.DeathTick + DangerZones.LifetimeTicks, result.RetryTick);
        Assert.Equal(["observe"], game.Calls);
        Assert.Equal("out-of-sight", journal.Single("danger-zone-inspection").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task ExpiredZoneWithoutVisibleMobileEnemiesNoLongerBlocksTheCorpse()
    {
        var game = new Game { Tick = Death.DeathTick + DangerZones.LifetimeTicks };
        var journal = new Journal();
        var travel = await Assert.ThrowsAsync<InvalidOperationException>(() => new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default));
        Assert.Contains("production_catalog", travel.Message);
        Assert.Equal(["observe", "production_catalog"], game.Calls);
        Assert.Empty(journal.All("danger-zone-inspection"));
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("unit-spawner")]
    public async Task ExpiredZoneWithCurrentMobileThreatsStillDefersWithoutApproaching(string type)
    {
        var game = new Game { Tick = Death.DeathTick + DangerZones.LifetimeTicks, Enemies = [(type, new(24, 0))] };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(result.Tick + CorpseRecoveryController.VisibleThreatRetryTicks, result.RetryTick);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Empty(result.Collected);
        Assert.Equal(["observe"], game.Calls);
        Assert.Empty(journal.All("danger-zone-inspection"));
        Assert.Single(journal.All("corpse-recovery-visible-threats"));
    }

    [Fact]
    public async Task TruncatedEnemyObservationStillDefersOnPositiveMobileEvidence()
    {
        var game = new Game { Tick = Death.DeathTick + DangerZones.LifetimeTicks, Enemies = [("unit", new(24, 0))], EnemiesTruncated = true };
        var result = await new CorpseRecoveryController(game, new Journal()).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Empty(result.Collected);
        Assert.Equal(["observe"], game.Calls);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("unit-spawner")]
    public async Task AGuardWithinNormalInspectionSightIsDetectedBeforeTravellingIntoTheSmallerRadius(string type)
    {
        // Normal seed20261072: the next recovery started 43 tiles from a guarded body; its initial radius32 missed the pack.
        var game = new Game { Actor = new(-20, 0), Tick = Death.DeathTick + DangerZones.LifetimeTicks,
            Enemies = [(type, new(24, 0))], LimitEnemiesToRequestedRadius = true };
        var result = await new CorpseRecoveryController(game, new Journal()).RunAsync(Death, Scope, default);

        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Empty(result.Collected);
        Assert.Equal(["observe"], game.Calls);
        Assert.Equal([CorpseRecoveryController.InspectionRadius], game.ObservedRadii);
    }

    [Fact]
    public async Task OlderCorpseBehindAnActiveZoneWaitsToo()
    {
        // The corpse of an earlier death lies outside every active zone, but the straight way to it crosses the latest one,
        // whose own corpse is already empty.
        var older = new NativeDeathTransition(1, 1000, 16, 1, new(80, 0));
        var latest = Death with { Incarnation = 2 };
        var game = new Game
        {
            Actor = new(-30, 0), Corpses = [older], Zones = DangerZones.Empty("world").Record(latest),
            ObservedScope = Scope with { Incarnation = 3 }
        };
        var result = await new CorpseRecoveryController(game, new Journal()).RunAsync(latest, game.ObservedScope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(latest.DeathTick + DangerZones.LifetimeTicks, result.RetryTick);
        Assert.Equal(["observe"], game.Calls);
    }

    [Fact]
    public void CorpsesFarFromKnownIndustryWaitWhileNearOnesAreRecovered()
    {
        // Campaign 2026-10-01 (seed 20261002): an old corpse far north-east, its zone expired, killed the recovering actor.
        var industry = CorpseRecoveryController.Industry(Protocol.ToElement(new
        {
            entities = new object[]
            {
                new { type = "furnace", position = new MapPosition(-50, 0) },
                new { type = "electric-pole", position = new MapPosition(60, -100) },
                new { type = "character-corpse", position = new MapPosition(96, -114) }
            }
        }));
        Assert.Equal([new MapPosition(-50, 0)], industry);
        Assert.True(CorpseRecoveryController.NearIndustry(industry, new(-20, 10)));
        Assert.False(CorpseRecoveryController.NearIndustry(industry, new(96, -114)));
        Assert.True(CorpseRecoveryController.NearIndustry([], new(96, -114)));
    }

    [Fact]
    public async Task FiniteNativeCorpseLifetimeIsJournaledAsALossRatherThanApproached()
    {
        // Base 2.0.77 character corpses never expire (time_to_live 0); a modded ten-minute body would vanish first.
        var game = new Game { Lifetime = 36000, Enemies = [("unit", new(24, 0))] };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        var corpse = journal.Single("corpse-recovery-danger-deferral").GetProperty("corpses")[0];
        Assert.Equal(Death.DeathTick + 36000, corpse.GetProperty("corpseExpiresTick").GetInt64());
        Assert.Equal(40, corpse.GetProperty("lostBeforeApproach").GetProperty("iron-plate").GetInt64());
    }

    [Theory]
    [InlineData("unit", false)]
    [InlineData("unit", true)]
    [InlineData("unit-spawner", false)]
    [InlineData("unit-spawner", true)]
    public async Task AGuardRevealedDuringNativeMovementStopsItsKnownIdentityAndDefersBeforeCollecting(string type, bool loseCancellation)
    {
        var game = new Game { AllowTravel = true, LoseCancellation = loseCancellation, Tick = 120000,
            Enemies = [(type, new(24, 0))] };
        var journal = new Journal();

        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);

        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(result.Tick + CorpseRecoveryController.VisibleThreatRetryTicks, result.RetryTick);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Empty(result.Collected);
        Assert.Equal(1, game.Submissions);
        Assert.Equal(1, game.Cancellations);
        Assert.Single(journal.All("corpse-recovery-approach-deferred"));
        Assert.Equal("cancelled", journal.Single("final-receipt").GetProperty("status").GetString());
        Assert.DoesNotContain("factory_snapshot", game.Calls); // No collection preparation after the guard appears.
    }

    [Fact]
    public async Task AGuardSeenJustAfterArrivalIsReobservedBeforeReadingOrTakingTheCorpseInventory()
    {
        var game = new Game { AllowTravel = true, CompleteMove = true, Tick = 120000, Enemies = [("unit", new(24, 0))] };
        var result = await new CorpseRecoveryController(game, new Journal()).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(1, game.Submissions);
        Assert.Equal(0, game.Cancellations);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.DoesNotContain("factory_snapshot", game.Calls);
    }

    [Fact]
    public async Task AnotherIncarnationDuringTheApproachCannotBeReportedAsACurrentGuardVerdict()
    {
        var game = new Game { AllowTravel = true, ChangeScopeDuringMove = true, Tick = 120000,
            Enemies = [("unit", new(24, 0))] };
        var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default));
        Assert.Equal(1, game.Submissions);
        Assert.Equal(1, game.Cancellations);
        Assert.Empty(journal.All("corpse-recovery-result"));
    }

    [Fact]
    public async Task AStoppedGuardedApproachIsNotRetriedWhenTheActorLosesSightOfTheGuard()
    {
        var game = new Game { AllowTravel = true, Tick = 120000, Enemies = [("unit", new(24, 0))],
            HideEnemiesAfterCancellation = true, ActorAfterCancellation = new(-50, 0) };
        var journal = new Journal();
        var result = await new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default);
        Assert.Equal("unsafe-corpses-deferred", result.Outcome);
        Assert.Equal(1, game.Submissions);
        Assert.Equal(1, game.Cancellations);
        Assert.Equal(40, result.Remaining["iron-plate"]);
        Assert.Empty(result.Collected);
        Assert.Single(journal.All("corpse-recovery-remembered-threats"));
        Assert.DoesNotContain("factory_snapshot", game.Calls);
    }

    private sealed class Journal : IControllerJournal
    {
        private readonly List<(string Type, JsonElement Data)> rows = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            rows.Add((type, Protocol.ToElement(data)));
            return Task.CompletedTask;
        }
        public IEnumerable<JsonElement> All(string type) => rows.Where(r => r.Type == type).Select(r => r.Data);
        public JsonElement Single(string type) => All(type).Single();
    }

    private sealed class Game : IGameClient, IDangerZoneReader
    {
        public MapPosition Actor { get; init; } = new(0, 0);
        public long Tick { get; set; } = 50000;
        public long? Lifetime { get; init; } = 0;
        public IReadOnlyList<(string Type, MapPosition Position)> Enemies { get; init; } = [];
        public bool EnemiesTruncated { get; init; }
        public bool LimitEnemiesToRequestedRadius { get; init; }
        public bool AllowTravel { get; init; }
        public bool AllowDefense { get; init; }
        public bool LoseDefenseReply { get; init; }
        public bool ChangeScopeAfterDefense { get; init; }
        public bool LoseCancellation { get; init; }
        public bool CompleteMove { get; init; }
        public bool ChangeScopeDuringMove { get; init; }
        public bool HideEnemiesAfterCancellation { get; init; }
        public MapPosition? ActorAfterCancellation { get; init; }
        private string? operationId;
        private string operationStatus = "running";
        public int Submissions { get; private set; }
        public int Cancellations { get; private set; }
        public IReadOnlyList<NativeDeathTransition> Corpses { get; init; } = [Death];
        public DangerZones Zones { get; init; } = DangerZones.Empty("world").Record(Death);
        public ActorScope ObservedScope { get; init; } = Scope;
        public List<string> Calls { get; } = [];
        public List<int> ObservedRadii { get; } = [];
        public List<int> SubmissionsAtDeathReads { get; } = [];

        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default)
        {
            SubmissionsAtDeathReads.Add(Submissions);
            return Task.FromResult(Zones.Active(surfaceIndex, tick));
        }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            // Travel starts by reading the production catalog: stop there, the approach decision is made.
            long tick = ++Tick;
            var actor = Cancellations > 0 ? ActorAfterCancellation ?? Actor : Actor;
            if (AllowDefense && request.Action is "submit" or "operation")
            {
                if (request.Action == "submit")
                {
                    Assert.Equal("shoot", request.Arguments.GetProperty("kind").GetString());
                    Assert.Equal("enemy-0", request.Arguments.GetProperty("args").GetProperty("entityId").GetString());
                    operationId = request.Arguments.GetProperty("operationId").GetString();
                    Submissions++;
                    if (LoseDefenseReply) throw new IOException("Shoot applied but response lost.");
                }
                else Assert.Equal(operationId, request.Arguments.GetProperty("operationId").GetString());
                return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new
                {
                    operationId, kind = "shoot", status = "completed", acceptedTick = tick, updatedTick = tick,
                    effects = new { roundsConsumed = 3 }
                })));
            }
            if (AllowTravel && request.Action != "observe")
            {
                var map = SpatialPlannerTests.Map([]) with
                {
                    Scope = ObservedScope, CollectedTick = tick,
                    Bounds = new(new(-32, -32), new(33, 33)),
                    Rows = Enumerable.Range(-32, 65).Select(y => new TileRun(-32, y, 65, "grass")).ToArray()
                };
                object result;
                switch (request.Action)
                {
                    case "production_catalog": result = Catalogs.Raw() with { Scope = ObservedScope, CollectedTick = tick }; break;
                    case "spatial": result = map with { Actor = map.Actor with { Position = CompleteMove && operationId is not null ? Death.Position : Actor } }; break;
                    case "submit":
                        Assert.Equal("move", request.Arguments.GetProperty("kind").GetString());
                        operationId = request.Arguments.GetProperty("operationId").GetString();
                        Submissions++;
                        if (CompleteMove) operationStatus = "completed";
                        result = Receipt(); break;
                    case "operation": Assert.Equal(operationId, request.Arguments.GetProperty("operationId").GetString()); result = Receipt(); break;
                    case "cancel":
                        Assert.Equal(operationId, request.Arguments.GetProperty("operationId").GetString());
                        Cancellations++;
                        operationStatus = "cancelled";
                        if (LoseCancellation) throw new IOException("Cancel applied but response lost.");
                        result = Receipt(); break;
                    default: throw new InvalidOperationException(request.Action);
                }
                return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(result)));
                object Receipt() => new { operationId, kind = "move", status = operationStatus, acceptedTick = 120000, updatedTick = tick, effects = new { } };
            }
            if (request.Action != "observe") throw new InvalidOperationException($"Approach requested with {request.Action}.");
            int radius = request.Arguments.GetProperty("radius").GetInt32();
            ObservedRadii.Add(radius);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new
            {
                scope = (ChangeScopeDuringMove || ChangeScopeAfterDefense) && operationId is not null ? ObservedScope with { Incarnation = ObservedScope.Incarnation + 1 } : ObservedScope, collectedTick = tick,
                coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick, radius, enemiesTruncated = EnemiesTruncated,
                    enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = actor, health = 250.0,
                    weapon = new { ready = true, rounds = 100, range = 15.0 }, inventory = new { }, reachDistance = 10.0 },
                enemies = Enemies.Where(e => (!AllowTravel || operationId is not null) && !(HideEnemiesAfterCancellation && Cancellations > 0)
                    && !(AllowDefense && Submissions > 0 && e.Position.DistanceTo(Actor) <= 15)
                    && (!LimitEnemiesToRequestedRadius || e.Position.DistanceTo(actor) <= radius))
                    .Select((e, index) => new { id = $"enemy-{index}", type = e.Type, position = e.Position, collectedTick = tick }),
                recovery = new
                {
                    knownCorpsesComplete = true,
                    corpses = Corpses.Select(c => new
                    {
                        id = $"corpse:{c.ActorUnitNumber}:{c.DeathTick}:1", incarnation = c.Incarnation, deathTick = c.DeathTick,
                        actorUnitNumber = c.ActorUnitNumber, surfaceIndex = 1, position = c.Position, timeToLive = Lifetime,
                        inventories = new { corpse = new { items = new Dictionary<string, long> { ["iron-plate"] = 40 } } }
                    })
                }
            })));
        }
    }
}
