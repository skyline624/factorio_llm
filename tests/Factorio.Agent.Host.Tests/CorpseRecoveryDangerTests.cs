using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

/// <summary>Campaign 2026-10-01 (seed 20261002): every recovery walked back to the fresh corpse and died near it.</summary>
public sealed class CorpseRecoveryDangerTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 2, 4);
    private static readonly NativeDeathTransition Death = new(1, 49000, 17, 1, new(20, 0));

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
    public async Task ExpiredZoneNoLongerBlocksTheCorpse()
    {
        var game = new Game { Tick = Death.DeathTick + DangerZones.LifetimeTicks, Enemies = [("unit", new(24, 0))] };
        var journal = new Journal();
        var travel = await Assert.ThrowsAsync<InvalidOperationException>(() => new CorpseRecoveryController(game, journal).RunAsync(Death, Scope, default));
        Assert.Contains("production_catalog", travel.Message);
        Assert.Equal(["observe", "production_catalog"], game.Calls);
        Assert.Empty(journal.All("danger-zone-inspection"));
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
        public IReadOnlyList<NativeDeathTransition> Corpses { get; init; } = [Death];
        public DangerZones Zones { get; init; } = DangerZones.Empty("world").Record(Death);
        public ActorScope ObservedScope { get; init; } = Scope;
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default) => Task.FromResult(Zones.Active(surfaceIndex, tick));

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            // Travel starts by reading the production catalog: stop there, the approach decision is made.
            if (request.Action != "observe") throw new InvalidOperationException($"Approach requested with {request.Action}.");
            long tick = ++Tick;
            int radius = request.Arguments.GetProperty("radius").GetInt32();
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new
            {
                scope = ObservedScope, collectedTick = tick,
                coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick, radius, enemiesTruncated = false,
                    enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Actor, health = 250.0,
                    weapon = new { ready = true, rounds = 100, range = 15.0 }, inventory = new { }, reachDistance = 10.0 },
                enemies = Enemies.Select((e, index) => new { id = $"enemy-{index}", type = e.Type, position = e.Position, collectedTick = tick }),
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
