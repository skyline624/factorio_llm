using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NestClearingTests
{
    private static readonly IndustryCluster[] Industry = [new("base", new(new(0, 0), new(10, 10)), ["1"])];

    private static NestMemory Memory() => new(1, "world", [
        new("near-spawner", "biter-spawner", new(100, 0), null, 1), new("near-worm", "small-worm-turret", new(108, 4), 25, 1),
        new("far-spawner", "biter-spawner", new(500, 0), null, 1)]);

    [Fact]
    public void TheRememberedBaseClosestToIndustryIsChosenWithinReach()
    {
        var chosen = NestClearingController.Choose(Memory(), Industry, new(1, "world", []), 1000);
        Assert.NotNull(chosen);
        Assert.Equal(["near-spawner", "near-worm"], chosen.Structures.Select(s => s.Id));
        Assert.Null(NestClearingController.Choose(Memory(), [], new(1, "world", []), 1000));
    }

    [Fact]
    public void APlannedCellBringsADistantGuardingNestWithinReach()
    {
        Assert.DoesNotContain(NestClearingController.Choose(Memory(), Industry, new(1, "world", []), 1000)!.Structures, s => s.Id == "far-spawner");
        var chosen = NestClearingController.Choose(Memory(), [], new(1, "world", []), 1000, [new(480, 10)]);
        Assert.Equal(["far-spawner"], chosen!.Structures.Select(s => s.Id));
    }

    [Fact]
    public void AFailedBaseWaitsForItsRetryDelayWhileAClearedOneDoesNotBlock()
    {
        var center = Memory().Clusters().Single(c => c.Structures.Count == 2).Center;
        var failed = new NestClearingState(1, "world", [new(center, 1000, "turret-losses")]);
        Assert.Null(NestClearingController.Choose(Memory(), Industry, failed, 1000 + NestClearingController.RetryTicks - 1));
        Assert.NotNull(NestClearingController.Choose(Memory(), Industry, failed, 1000 + NestClearingController.RetryTicks));
        var cleared = new NestClearingState(1, "world", [new(center, 1000, "cleared")]);
        Assert.NotNull(NestClearingController.Choose(Memory(), Industry, cleared, 1001));
    }

    [Fact]
    public void AStructureThatDestroysAWholeBlockUnharmedIsRememberedWithItsAmmunition()
    {
        // Seed 20261106 on 2026-10-08: four turrets with basic rounds were lost to a medium worm without damaging it to death.
        var state = new NestClearingState(1, "world", []);
        var beaten = new NestClearingStep("w", "medium-worm-turret", ["t1", "t2", "t3", "t4"], 6.5, 0, 4, 0, "turrets-destroyed");
        var killed = new NestClearingStep("s", "spitter-spawner", ["t5", "t6", "t7", "t8"], 0, 1, 1, 3, "target-destroyed");
        var result = new NestClearingResult("turret-losses", new(0, 0), 1, 1, 1, 5, [killed, beaten], 900, "basic-rounds");
        var after = NestClearingController.WithOvermatches(state, result);
        Assert.Equal([("medium-worm-turret", "basic-rounds", 900L)], after.Overmatched!.Select(o => (o.Structure, o.Ammunition, o.Tick)));
        // A result without a known ammunition (e.g. no stock) teaches nothing.
        Assert.Null(NestClearingController.WithOvermatches(state, result with { Ammunition = null }).Overmatched);
    }

    [Fact]
    public void AnAttemptThatNeverReportedBackDelaysItsRetryAndIsReplacedByItsOutcome()
    {
        // Seed 20261107 on 2026-10-08: the actor died at a worm, nothing was recorded, and it returned to the same worm and died again.
        var center = Memory().Clusters().Single(c => c.Structures.Count == 2).Center;
        var started = NestClearingController.WithAttempt(new NestClearingState(1, "world", []),
            new(center, 1000, NestClearingController.StartedOutcome));
        Assert.Null(NestClearingController.Choose(Memory(), Industry, started, 1001));
        // An attempt that never reported back (usually a death) waits far longer than an ordinary failure.
        Assert.Null(NestClearingController.Choose(Memory(), Industry, started, 1000 + NestClearingController.RetryTicks));
        Assert.NotNull(NestClearingController.Choose(Memory(), Industry, started, 1000 + NestClearingController.DeathRetryTicks));
        var finished = NestClearingController.WithAttempt(started, new(center, 1200, "cleared"));
        Assert.Equal([("cleared", 1200L)], finished.Attempts.Select(a => (a.Outcome, a.Tick)));
    }

    [Fact]
    public void TheStrongestCompatibleStockedOrProducibleRoundsComeFirst()
    {
        var (_, catalog, _, _) = PortableDefenseTests.Fixture();
        var turret = catalog.Turrets!["native-turret"];
        Assert.Equal(["strong-rounds", "basic-rounds"], NestClearingController.StrongestAmmunition(turret, catalog,
            new Dictionary<string, long> { ["basic-rounds"] = 60, ["strong-rounds"] = 5, ["incompatible-rounds"] = 9 }));
        Assert.Equal(["basic-rounds"], NestClearingController.StrongestAmmunition(turret, catalog,
            new Dictionary<string, long> { ["basic-rounds"] = 60 }));
    }

    [Fact]
    public void AnAssaultNeverFallsBackToWeakerRounds()
    {
        string[] choices = ["piercing-rounds-magazine", "firearm-magazine"];
        Assert.Equal("piercing-rounds-magazine", NestClearingController.Armament(choices, _ => true));
        // Seed 20261107, 2026-10-08: piercing rounds lacked steel and firearm magazines lost the actor to medium biters.
        Assert.Null(NestClearingController.Armament(choices, a => a == "firearm-magazine"));
        Assert.Equal("firearm-magazine", NestClearingController.Armament(["firearm-magazine"], _ => true));
        Assert.Null(NestClearingController.Armament([], _ => true));
    }

    [Fact]
    public void TheScoutingRingStartsTowardTheLatestAttackAroundIndustry()
    {
        var attacks = new AttackLogState(1, "world", [
            new(100, "base", 3, 0, 0, 0, "W", [new(-50, 5)], []),
            new(50, "base", 1, 0, 0, 0, "E", [new(80, 5)], [])], new([], new Dictionary<string, double>(), [], 0));
        var ring = NestClearingController.ScoutRing(Industry, attacks);
        Assert.Equal(NestClearingController.ScoutPoints, ring.Count);
        var center = new MapPosition(5, 5);
        Assert.All(ring, p => Assert.InRange(p.DistanceTo(center), 127, 130));
        Assert.True(ring[0].X < center.X - 120, $"first point {ring[0]} faces the latest (western) attack");
        Assert.Equal(ring.Count, ring.Distinct().Count());
    }

    [Fact]
    public void DefensiveTurretsTakeTheStrongestRoundsAlreadyInStock()
    {
        var (_, catalog, _, _) = PortableDefenseTests.Fixture();
        var turret = catalog.Turrets!["native-turret"];
        Assert.Equal("strong-rounds", DefenseDeploymentPlanner.ChooseAmmunition(turret, catalog,
            new Dictionary<string, long> { ["basic-rounds"] = 60, ["strong-rounds"] = 5 }));
        Assert.Equal("basic-rounds", DefenseDeploymentPlanner.ChooseAmmunition(turret, catalog, new Dictionary<string, long> { ["basic-rounds"] = 60 }));
    }

    [Fact]
    public void AStandBeyondThePhotographIsApproachedAlongItsLineInsideTheView()
    {
        var map = SpatialPlannerTests.Map([]);
        Assert.Equal(new MapPosition(8, 0), NestClearingController.Toward(map, new(100, 0)));
        Assert.Equal(new MapPosition(0, -8), NestClearingController.Toward(map, new(0, -100)));
        Assert.Throws<NavigationPlanningException>(() => NestClearingController.Toward(map with
            { Actor = map.Actor with { Position = new(9, 0) } }, new(100, 0)));
    }

    [Fact]
    public void StagingStandsTowardTheActorBeyondWormRangeAndTheTravelMargin()
    {
        var nest = Memory().Clusters().Single(c => c.Structures.Count == 2);
        var staging = NestClearingController.Staging(nest, new(0, 0));
        Assert.True(staging.X < nest.Center.X);
        Assert.All(nest.Structures.Where(s => s.Range is not null),
            worm => Assert.True(staging.DistanceTo(worm.Position) > worm.Range!.Value + ExplorationPlanner.ThreatMargin));
    }

    [Theory]
    [InlineData("completed", 1, 1)]
    [InlineData("cancelled", 0, 3)]
    [InlineData("failed", 0, 1)]
    public async Task OnlyACompletedPickupCountsAsRecoveredAndACancelledOneIsRetried(string status, int recovered, int submissions)
    {
        var game = new MineGame(status);
        await using var controller = new SpatialController(game, new NullJournal());
        var back = await NestClearingController.PickUpAsync(controller, ["turret-1"], default);
        Assert.Equal(recovered, back.Count);
        Assert.Equal(submissions, game.Mines);
    }

    private sealed class MineGame(string status) : IGameClient
    {
        private readonly SpatialSnapshot map = SpatialPlannerTests.Map([]);
        public int Mines { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            object data = request.Action switch
            {
                "spatial" => map,
                "observe" => new { map.Scope, collectedTick = map.CollectedTick,
                    coverage = new { atomic = true, collectionStartTick = map.CollectedTick, collectionEndTick = map.CollectedTick,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>() },
                "submit" => Submit(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(data)));
        }
        private object Submit(OperationSubmission operation)
        {
            Assert.Equal("mine", operation.Kind);
            Mines++;
            return new { operation.OperationId, operation.Kind, status, acceptedTick = map.CollectedTick, updatedTick = map.CollectedTick, effects = new { } };
        }
    }

    private sealed class NullJournal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
}
