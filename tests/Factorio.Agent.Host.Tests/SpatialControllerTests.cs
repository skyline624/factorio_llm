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
    public void CombinedMoveRemainsWithinEightTiles()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var points = SpatialController.Subdivide(field.Map.Actor.Position, [new(12, 0)]);
        var chosen = SpatialController.SelectWaypoint(field, new(RouteStatus.Found, points, 0, 12));
        Assert.InRange(chosen.DistanceTo(field.Map.Actor.Position), 7, 8);
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
