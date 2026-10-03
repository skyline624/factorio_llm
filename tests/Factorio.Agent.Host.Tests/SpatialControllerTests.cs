using Factorio.Agent.Core;
using Factorio.Agent.Host;
using System.Text.Json;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RespawnDuringSafetyObservationCannotDispatchAnOldNavigationOrWorkIntent(bool work)
    {
        var game = new RespawningGame(duringObservation: true);
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (work) await controller.WorkAsync("wait", new { ticks = 60 }, 600);
            else await controller.NavigateAsync(new(5, 0));
        });
        Assert.Equal(0, game.Submissions);
    }

    [Fact]
    public async Task DeathDuringMoveCannotContinueTheOldRouteFromTheRespawnLocation()
    {
        var game = new RespawningGame(duringObservation: false);
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.NavigateAsync(new(5, 0)));
        Assert.Equal(1, game.Submissions);
    }

    [Fact]
    public void OpenTerrainCombinesShortWaypointsIntoOneBoundedMove()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var points = SpatialController.Subdivide(field.Map.Actor.Position, [new(7, 3)]);
        var chosen = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 8));
        Assert.Equal(new MapPosition(7, 3), chosen);
    }

    [Fact]
    public void NativeSteeringCannotShortcutBesideAnObstacleOutsideTheStraightLine()
    {
        var map = SpatialPlannerTests.Map([]);
        var wall = new SpatialEntity("wall", "wall", new(3, 0), new(new(2.7, -.3), new(3.3, .3)), 0, "own");
        map = map with { Entities = [wall] };
        var field = new SpatialCollisionField(map);
        var points = SpatialController.Subdivide(map.Actor.Position, [new(7, 3)]);
        Assert.True(field.SegmentClear(map.Actor.Position, new(7, 3)));
        var chosen = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 8));
        Assert.True(chosen.X < wall.Bounds.Min.X);
    }

    [Fact]
    public void OpenCorridorNoLongerStopsAtEightTiles()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var points = SpatialController.Subdivide(field.Map.Actor.Position, [new(12, 0)]);
        var chosen = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 12));
        Assert.Equal(new MapPosition(12, 0), chosen);
        var baseline = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 12), 8);
        Assert.InRange(baseline.DistanceTo(field.Map.Actor.Position), 7, 8);
    }

    [Fact]
    public void ACornerOnAMovingBeltContinuesToStableGroundWithinOneMove()
    {
        var map = BeltCornerMap();
        var field = new SpatialCollisionField(map);
        var points = SpatialController.Subdivide(map.Actor.Position, [new(3.5, .5), new(3.5, 3.5)]);
        var route = new RoutePlan(RouteStatus.Found, points, 0, 6.5);
        Assert.Equal(new MapPosition(3.5, .5), SpatialController.SelectWaypoint(field, route));
        var path = SpatialController.SelectMovePath(field, route);
        Assert.Equal(new MapPosition[] { new(3.5, .5), new(3.5, 3.5) }, path);
        Assert.False(PlacementPlanner.CanStop(field, path[0]));
        Assert.True(PlacementPlanner.CanStop(field, path[^1]));
        MapPosition from = map.Actor.Position;
        foreach (var point in path) { Assert.True(field.SteeringRegionClear(from, point)); from = point; }
    }

    [Fact]
    public void ContinuousCornersRetainTheTotalDistanceBound()
    {
        var map = BeltCornerMap();
        var points = SpatialController.Subdivide(map.Actor.Position, [new(3.5, .5), new(3.5, 7.5)]);
        var path = SpatialController.SelectMovePath(new(map), new(RouteStatus.Found, points, 0, 10.5), 4);
        double length = map.Actor.Position.DistanceTo(path[0]) + path.Zip(path.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
        Assert.InRange(length, 0, 4.000000001);
        Assert.DoesNotContain(new MapPosition(3.5, 7.5), path);
    }

    [Fact]
    public void StableGroundRetainsTheExistingSingleMoveBehavior()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var points = SpatialController.Subdivide(field.Map.Actor.Position, [new(7, 3)]);
        Assert.Equal(new MapPosition[] { new(7, 3) }, SpatialController.SelectMovePath(field, new(RouteStatus.Found, points, 0, 8)));
    }

    private static SpatialSnapshot BeltCornerMap()
    {
        var map = SpatialPlannerTests.Map([]);
        return map with
        {
            ContinuousMovePaths = true,
            Actor = map.Actor with { Position = new(0, .5) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["belt"] = new("belt", "transport-belt", new(new(-.4, -.4), new(.4, .4)), new([], false, false, false), 1, 1) },
            Entities = [new("belt", "belt", new(3.5, .5), new(new(3.1, .1), new(3.9, .9)), 12, "agent"),
                new("wall", "wall", new(2, 1.5), new(new(1.8, 1.3), new(2.2, 1.7)), 0, "agent")]
        };
    }

    [Fact]
    public void AnOlderNativeApiCannotIgnoreCornersAndPursueOnlyTheFinalDestination()
    {
        var map = BeltCornerMap() with { ContinuousMovePaths = false };
        var points = SpatialController.Subdivide(map.Actor.Position, [new(3.5, .5), new(3.5, 3.5)]);
        var path = SpatialController.SelectMovePath(new(map), new(RouteStatus.Found, points, 0, 6.5));
        Assert.Equal(new MapPosition[] { new(3.5, .5) }, path);
        Assert.False(new SpatialCollisionField(map).SteeringRegionClear(map.Actor.Position, new(3.5, 3.5)));
    }

    [Fact]
    public void ContinuousMoveIgnoresAnObstacleAtACornerAlreadyPassedInTheNativeFrame()
    {
        var map = SpatialPlannerTests.Map([]);
        map = map with { Actor = map.Actor with { Position = new(3.5, 2), Movement = new("move", 2, 2) },
            Entities = [new("behind", "wall", new(3.5, .5), new(new(3.1, .1), new(3.9, .9)), 0, "agent")] };
        MapPosition[] path = [new(3.5, .5), new(3.5, 3.5)];
        Assert.False(new SpatialCollisionField(map).SteeringRegionClear(map.Actor.Position, path[0]));
        Assert.True(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(0, .5), path, out int leg));
        Assert.Equal(1, leg);
        map = map with { Entities = [.. map.Entities, new("ahead", "wall", new(3.5, 3), new(new(3.1, 2.6), new(3.9, 3.4)), 0, "agent")] };
        Assert.False(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(0, .5), path, out _));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-operation")]
    [InlineData("changed-scope")]
    public void ContinuousMoveRequiresItsOwnProgressInTheCurrentActorScope(string change)
    {
        var map = SpatialPlannerTests.Map([]);
        map = map with { Actor = map.Actor with { Movement = change == "missing" ? null : new(change == "different-operation" ? "other" : "move", 1, 2) } };
        var scope = change == "changed-scope" ? map.Scope with { Generation = map.Scope.Generation + 1 } : map.Scope;
        Assert.False(SpatialController.MovementPathIsClear(map, scope, "move", new(0, 0), [new(1, 0), new(2, 0)], out _));
    }

    [Theory]
    [InlineData(1, -.796875, -.06640625)]
    [InlineData(2, .025, -.125)]
    public void ContinuousRecheckRetainsShortSweptCornersSelectedBesideAnInserter(int index, double x, double y)
    {
        var map = TightBeltCornerMap();
        MapPosition start = map.Actor.Position;
        var points = SpatialController.Subdivide(start, [new(0, 0), new(.5, -2.5)]);
        var path = SpatialController.SelectMovePath(new(map), new(RouteStatus.Found, points, 0, 9));
        Assert.Equal(new MapPosition[] { new(0, 0), new(.125, -.625), new(.5, -2.5) }, path);
        var field = new SpatialCollisionField(map);
        Assert.False(field.SteeringRegionClear(path[0], path[1]));
        Assert.True(field.SegmentClear(path[0], path[1]));
        map = map with { Actor = map.Actor with { Position = new(x, y), Movement = new("move", index, path.Count) } };
        Assert.True(SpatialController.MovementPathIsClear(map, map.Scope, "move", start, path, out int leg));
        Assert.Equal(index - 1, leg);
    }

    private static SpatialSnapshot TightBeltCornerMap()
    {
        var map = BeltCornerMap();
        return map with
        {
            Actor = map.Actor with { Position = new(-6.25, -.37109375) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["inserter"] = new("inserter", "inserter", new(new(-.15, -.15), new(.15, .15)), map.Prototypes["chest"].Mask, 1, 1) },
            Entities = [new("belt", "belt", new(-.5, 0), new(new(-.9, -.4), new(-.1, .4)), 12, "agent"),
                new("inserter", "inserter", new(-.5, -1), new(new(-.65, -1.15), new(-.35, -.85)), 0, "agent")]
        };
    }

    [Theory]
    [InlineData("wall")]
    [InlineData("water")]
    [InlineData("threat")]
    public void TightCornerRecheckStillRefusesANewPhysicalObstruction(string change)
    {
        var map = TightBeltCornerMap();
        MapPosition start = map.Actor.Position;
        MapPosition[] path = [new(0, 0), new(.125, -.625), new(.25, -1.25)];
        map = map with { Actor = map.Actor with { Position = new(-.8, 0), Movement = new("move", 1, 3) } };
        map = change switch
        {
            "wall" => map with { Entities = [.. map.Entities, new("new-wall", "wall", new(.125, -.625), new(new(.025, -.725), new(.225, -.525)), 0, "agent")] },
            "water" => map with { Rows = map.Rows.Select(r => r.Y == -1 ? new TileRun(-12, -1, 25, "water") : r).ToArray() },
            "threat" => map with { StationaryThreats = [new("worm", new(.125, -.625), 2, map.CollectedTick)] },
            _ => throw new InvalidOperationException(change)
        };
        Assert.False(SpatialController.MovementPathIsClear(map, map.Scope, "move", start, path, out _));
    }

    [Fact]
    public void ALongPlannedLegRetainsItsSteeringRectangleEvenNearItsDestination()
    {
        var map = TightBeltCornerMap();
        map = map with { Actor = map.Actor with { Position = new(0, 0), Movement = new("move", 1, 2) } };
        MapPosition[] path = [new(.125, -.625), new(.25, -1.25)];
        Assert.True(new SpatialCollisionField(map).SegmentClear(map.Actor.Position, path[0]));
        Assert.False(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(0, 5), path, out _));
    }

    [Fact]
    public void AShortCornerCannotAcceptBeltDriftBeyondItsArrivalAllowance()
    {
        var map = TightBeltCornerMap();
        map = map with { Actor = map.Actor with { Position = new(-1, 0), Movement = new("move", 2, 3) } };
        Assert.False(SpatialController.MovementPathIsClear(map, map.Scope, "move", new(-6.25, 0),
            [new(0, 0), new(.125, -.625), new(.25, -1.25)], out _));
    }

    [Fact]
    public void LongerMovesRemainBoundedToTwentyFourKnownClearTiles()
    {
        var map = SpatialPlannerTests.Map([]) with
        {
            Bounds = new(new(-32, -32), new(33, 33)),
            Rows = Enumerable.Range(-32, 65).Select(y => new TileRun(-32, y, 65, "grass")).ToArray()
        };
        var field = new SpatialCollisionField(map);
        var points = SpatialController.Subdivide(map.Actor.Position, [new(30, 0)]);
        var chosen = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 30));
        Assert.InRange(chosen.DistanceTo(map.Actor.Position), 23, 24);
    }

    [Fact]
    public void Long_oblique_route_is_subdivided_before_native_eight_direction_steering()
    {
        var start = new MapPosition(-8, -63);
        var goal = new MapPosition(-20, -40);
        IReadOnlyList<MapPosition> route = SpatialController.Subdivide(start, [goal]);
        Assert.Equal(goal, route[^1]);
        MapPosition previous = start;
        foreach (MapPosition point in route)
        {
            Assert.InRange(previous.DistanceTo(point), 0, 0.75000001);
            Assert.InRange(Math.Abs((point.X - start.X) * (goal.Y - start.Y) - (point.Y - start.Y) * (goal.X - start.X)), 0, 1e-9);
            previous = point;
        }
    }

    [Fact]
    public void Reached_corner_is_not_resubmitted_as_a_zero_motion_operation()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var route = new RoutePlan(RouteStatus.Found, [new(0.05, 0), new(4, 3)], 1, 5);
        Assert.Equal(new MapPosition(4, 3), SpatialController.SelectWaypoint(field, route));
    }

    [Fact]
    public async Task ExtendedSearchFindsACollisionFreeDetourWithoutExecutingTheOldPhotograph()
    {
        var map = SpatialPlannerTests.Map([new("wall", "wall", new(3, 0), new(new(2.5, -2), new(3.5, 2)), 0, "own")]);
        var field = new SpatialCollisionField(map);
        var game = new PlanningGame(map);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);

        var route = await controller.DeepenRouteAsync(field, new(8, 0), .4, CancellationToken.None);

        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.True(route.Length > 8);
        MapPosition previous = map.Actor.Position;
        foreach (var point in route.Waypoints)
        {
            Assert.True(field.SegmentClear(previous, point));
            previous = point;
        }
        Assert.Equal(new MapPosition(8, 0), previous);
        Assert.All(game.SubmittedKinds, kind => Assert.Equal("wait", kind));
        Assert.Contains("route-search-deepened", journal.Types);
    }

    [Fact]
    public async Task CancelledExtendedSearchCannotDispatchAnActorOperation()
    {
        var map = SpatialPlannerTests.Map([]);
        var game = new PlanningGame(map);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.DeepenRouteAsync(new(map), new(8, 0), .4,
            cancellation.Token));

        Assert.Empty(game.SubmittedKinds);
        Assert.DoesNotContain("route-search-deepened", journal.Types);
    }

    private sealed class PlanningGame(SpatialSnapshot map) : IGameClient
    {
        public List<string> SubmittedKinds { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object data = request.Action switch
            {
                "spatial" => map,
                "observe" => new
                {
                    map.Scope, collectedTick = map.CollectedTick,
                    coverage = new { atomic = true, collectionStartTick = map.CollectedTick, collectionEndTick = map.CollectedTick,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                    enemies = Array.Empty<object>()
                },
                "submit" => Submitted(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(data)));
        }
        private object Submitted(OperationSubmission operation)
        {
            SubmittedKinds.Add(operation.Kind);
            return new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = map.CollectedTick,
                updatedTick = map.CollectedTick, effects = new { } };
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Interrupted_move_is_stopped_by_identity_even_when_mutation_responses_are_lost(
        bool loseSubmission, bool loseCancellation)
    {
        using var interruption = new CancellationTokenSource();
        var game = new InterruptingGame(interruption, loseSubmission, loseCancellation);
        var journal = new Journal();
        await using (var controller = new SpatialController(game, journal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                controller.NavigateAsync(new(5, 0), cancellationToken: interruption.Token));
        }
        Assert.Equal("cancelled", game.Status);
        Assert.Single(game.Calls, action => action == "submit");
        Assert.Single(game.Calls, action => action == "cancel");
        Assert.Contains("operation", game.Calls);
        Assert.Equal(game.OperationId, game.CancelledId);
        Assert.Contains("final-receipt", journal.Types);
    }

    [Fact]
    public async Task MalformedReceiptStopsNavigationInsteadOfPollingUntilTheCallerDeadline()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var game = new MalformedReceiptGame();
        var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.NavigateAsync(new(5, 0), cancellationToken: guard.Token));
        // Disposal reports the still unproven stop instead of hiding it.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await controller.DisposeAsync());
        Assert.False(guard.IsCancellationRequested);
        Assert.InRange(game.Queries, 1, 20);
    }

    [Fact]
    public async Task AnOwnedOperationThatNeverReachedTheEngineDoesNotHideTheOriginalFailure()
    {
        using var interruption = new CancellationTokenSource();
        var game = new UnsubmittedGame(interruption);
        var journal = new Journal();
        var controller = new SpatialController(game, journal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.NavigateAsync(new(5, 0), cancellationToken: interruption.Token));
        await controller.DisposeAsync();
        Assert.Contains("owned-operation-unsubmitted", journal.Types);
        Assert.Equal(0, game.Cancels);
    }

    /// <summary>The submit call is cancelled before it reaches the engine, which therefore knows no such operation.</summary>
    private sealed class UnsubmittedGame(CancellationTokenSource interruption) : IGameClient
    {
        public int Cancels { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            SpatialSnapshot map = SpatialPlannerTests.Map([]);
            if (request.Action == "submit")
            {
                interruption.Cancel();
                throw new OperationCanceledException(interruption.Token);
            }
            if (request.Action == "cancel") Cancels++;
            if (request.Action is "operation" or "cancel")
                return Task.FromResult(new GameResponse(1, request.RequestId, false, 100, Protocol.ToElement(new { }),
                    new GameError("operation_unknown", "Receipt absent or expired; reconcile before retrying")));
            object data = request.Action switch
            {
                "observe" => new
                {
                    map.Scope, collectedTick = 100,
                    coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                    enemies = Array.Empty<object>()
                },
                "spatial" => map,
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }
    }

    private sealed class MalformedReceiptGame : IGameClient
    {
        private string? operationId;
        public int Queries { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpatialSnapshot map = SpatialPlannerTests.Map([]);
            object data = request.Action switch
            {
                "observe" => new
                {
                    map.Scope, collectedTick = 100,
                    coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                    enemies = Array.Empty<object>()
                },
                "spatial" => map,
                "submit" => Receipt(request.Arguments.GetProperty("operationId").GetString(), "running"),
                "operation" => Receipt(operationId, ++Queries > 0 ? "not-a-native-status" : "running"),
                "cancel" => Receipt(operationId, "cancelled"),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }

        private object Receipt(string? id, string status)
        {
            operationId = id;
            return new { operationId = id, kind = "move", status, acceptedTick = 100, updatedTick = 100, effects = new { } };
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("obstacle", false)]
    [InlineData("obstacle", true)]
    [InlineData("scope", false)]
    [InlineData("manual", false)]
    [InlineData("threat", false)]
    public async Task LongMovementRechecksTheWorldAndStopsOnceWhenItsCorridorChanges(string change, bool loseCancellation)
    {
        using var interruption = new CancellationTokenSource();
        var game = new ChangingCorridorGame(change, loseCancellation, interruption);
        var journal = new Journal();
        await using (var controller = new SpatialController(game, journal))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.NavigateAsync(new(12, 0), cancellationToken: interruption.Token));
        Assert.Equal(1, game.Submissions);
        Assert.Equal(1, game.Cancellations);
        Assert.True(game.InFlightSpatialReads > 0);
        Assert.Contains("movement-terrain-check", journal.Types);
        Assert.Contains("final-receipt", journal.Types);
        Assert.Equal("cancelled", game.Status);
    }

    private sealed class ChangingCorridorGame(string change, bool loseCancellation, CancellationTokenSource interruption) : IGameClient
    {
        private long tick = 100;
        private string? operationId;
        public string Status { get; private set; } = "running";
        public int Submissions { get; private set; }
        public int Cancellations { get; private set; }
        public int InFlightSpatialReads { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var map = SpatialPlannerTests.Map([]) with { CollectedTick = tick };
            object data;
            switch (request.Action)
            {
                case "spatial":
                    if (operationId is not null)
                    {
                        InFlightSpatialReads++;
                        map = change switch
                        {
                            "obstacle" => map with { Entities = [new("new-wall", "wall", new(6, 0), new(new(5.5, -.5), new(6.5, .5)), 0, "own")] },
                            "scope" => map with { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } },
                            "manual" => map with { Actor = map.Actor with { ControlMode = "manual" } },
                            "threat" => map with { StationaryThreats = [new("worm", new(12, 5), 7, tick)] },
                            _ => throw new InvalidOperationException(change)
                        };
                    }
                    data = map;
                    break;
                case "observe":
                    data = new
                    {
                        map.Scope, collectedTick = tick,
                        coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                        enemies = Array.Empty<object>()
                    };
                    break;
                case "submit":
                    operationId = request.Arguments.GetProperty("operationId").GetString();
                    Submissions++;
                    data = Receipt();
                    break;
                case "operation": tick += 40; data = Receipt(); break;
                case "cancel":
                    Assert.Equal(operationId, request.Arguments.GetProperty("operationId").GetString());
                    Cancellations++;
                    Status = "cancelled";
                    interruption.Cancel();
                    if (loseCancellation) throw new IOException("Cancellation accepted, reply lost.");
                    data = Receipt();
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }

        private object Receipt() => new { operationId, kind = "move", status = Status, acceptedTick = 100, updatedTick = tick, effects = new { } };
    }

    private sealed class RespawningGame(bool duringObservation) : IGameClient
    {
        private bool respawned;
        private MapPosition position = new(0, 0);
        public int Submissions { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Action == "observe" && duringObservation) respawned = true;
            var original = SpatialPlannerTests.Map([]);
            var map = original with
            {
                Scope = original.Scope with { Incarnation = respawned ? 2 : 1, Generation = respawned ? 2 : 1 },
                Actor = original.Actor with { Position = position }
            };
            object data;
            switch (request.Action)
            {
                case "spatial": data = map; break;
                case "observe":
                    data = new
                    {
                        map.Scope, collectedTick = 100,
                        coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                        enemies = Array.Empty<object>()
                    };
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Submissions++;
                    bool died = !duringObservation && Submissions == 1;
                    if (died) respawned = true;
                    else if (submission.Args.TryGetProperty("position", out var destination))
                        position = destination.Deserialize<MapPosition>(Protocol.Json)!;
                    data = new { submission.OperationId, submission.Kind, status = died ? "cancelled" : "completed",
                        acceptedTick = 100, updatedTick = 100, effects = new { },
                        error = died ? new { code = "actor_dead", message = "Native death cancelled the operation." } : null };
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }
    }

    private sealed class InterruptingGame(CancellationTokenSource interruption, bool loseSubmit, bool loseCancel) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public string? OperationId { get; private set; }
        public string? CancelledId { get; private set; }
        public string Status { get; private set; } = "running";

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request.Action);
            SpatialSnapshot map = SpatialPlannerTests.Map([]);
            object data;
            switch (request.Action)
            {
                case "observe":
                    data = new
                    {
                        map.Scope, collectedTick = 100,
                        coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                        enemies = Array.Empty<object>()
                    };
                    break;
                case "spatial": data = map; break;
                case "submit":
                    OperationId = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!.OperationId;
                    interruption.Cancel();
                    if (loseSubmit) throw new IOException("Accepted move, response lost.");
                    data = Receipt();
                    break;
                case "operation":
                    Assert.Equal(OperationId, request.Arguments.GetProperty("operationId").GetString());
                    data = Receipt();
                    break;
                case "cancel":
                    CancelledId = request.Arguments.GetProperty("operationId").GetString();
                    Status = "cancelled";
                    if (loseCancel) throw new IOException("Stopped move, response lost.");
                    data = Receipt();
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }

        private object Receipt() => new { operationId = OperationId, kind = "move", status = Status,
            acceptedTick = 100, updatedTick = 100, effects = new { } };
    }
}
