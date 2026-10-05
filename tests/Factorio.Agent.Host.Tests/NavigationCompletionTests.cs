using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NavigationCompletionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InteractionApproachLeavesATightStartBeyondNativeCompletionTolerance(bool continuousPaths, bool replayNativeStops)
    {
        // Seed20261072 stopped at(-71.9375,-55.875); the only returned seed(-72,-56) was .13975 tiles away.
        var game = new Game(continuousPaths, replayNativeStops);
        var initial = new SpatialCollisionField(game.Map);
        Assert.False(PlacementPlanner.CanStop(initial, game.Position));
        var previous = new RoutePlanner().Find(initial, new(-5.5, 1.5), 7.8, requireStableArrival: true);
        Assert.Equal(RouteStatus.Found, previous.Status);
        Assert.True(game.Position.DistanceTo(previous.Waypoints[0]) <= .15);

        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var result = await controller.NavigateAsync(new(-5.5, 1.5), 8);

        Assert.InRange(game.Submissions.Count, 1, 8);
        var excluded = new HashSet<MapPosition>();
        foreach (var entry in journal.Entries)
        {
            if (entry.Type == "unstable-movement-arrival")
                excluded.Add(entry.Data.GetProperty("waypoint").Deserialize<MapPosition>(Protocol.Json)!);
            if (entry.Type == "route-plan")
            {
                var route = entry.Data.GetProperty("route").Deserialize<RoutePlan>(Protocol.Json)!;
                // The same corner may be needed for transit, but an already failed handoff must not
                // become the route's final arrival again. Reusing the remaining safe route is allowed.
                Assert.DoesNotContain(route.Waypoints[^1], excluded);
            }
        }
        if (replayNativeStops) Assert.True(excluded.Count >= 2);
        Assert.True(game.InitialPosition.DistanceTo(game.Submissions[0].Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!) > .15);
        Assert.True(game.Position.DistanceTo(game.InitialPosition) > .15);
        Assert.True(PlacementPlanner.CanStop(new(game.Map), result.Position));
        Assert.True(result.Position.DistanceTo(new(-5.5, 1.5)) <= 8);
        Assert.DoesNotContain(game.Calls, c => c is "mine" or "craft" or "cancel");
    }

    [Fact]
    public async Task DeepenedSearchAlsoReturnsAnExecutableFirstStepFromTheSameTightStart()
    {
        var game = new Game(true);
        var field = new SpatialCollisionField(game.Map);
        await using var controller = new SpatialController(game, new Journal());
        var excluded = new HashSet<MapPosition> { new(0, .5) };
        var route = await controller.DeepenRouteAsync(field, new(-5.5, 1.5), 8, default, excluded);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.DoesNotContain(route.Waypoints[^1], excluded);
        Assert.True(field.Map.Actor.Position.DistanceTo(route.Waypoints[0]) > .15);
        var path = SpatialController.SelectMovePath(field, route);
        Assert.True(PlacementPlanner.CanStop(field, path[^1]));
        Assert.All(game.Submissions, submission => Assert.Equal("wait", submission.Kind));
    }

    private sealed class Journal : IControllerJournal
    {
        public List<(string Type, JsonElement Data)> Entries { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Entries.Add((type, Protocol.ToElement(data)));
            return Task.CompletedTask;
        }
    }

    private sealed class Game(bool continuousPaths, bool replayNativeStops = false) : IGameClient
    {
        public MapPosition InitialPosition { get; } = new(.0625, .125);
        public MapPosition Position { get; private set; } = new(.0625, .125);
        public List<string> Calls { get; } = [];
        public List<OperationSubmission> Submissions { get; } = [];
        private long tick = 100;
        private OperationReceipt? receipt;
        public SpatialSnapshot Map => SpatialPlannerTests.Map(
            [new("corner", "wall", new(.5, .5), new(new(.4, .4), new(.6, .6)), 0, "agent")]) with
        {
            CollectedTick = tick, ContinuousMovePaths = continuousPaths,
            Actor = SpatialPlannerTests.Map([]).Actor with { Position = Position }
        };

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request.Action);
            tick++;
            object data;
            switch (request.Action)
            {
                case "spatial": data = Map; break;
                case "observe":
                    var view = new Dictionary<string, object>
                    {
                        ["scope"] = Map.Scope, ["collectedTick"] = tick,
                        ["coverage"] = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemiesTruncated = false },
                        ["agent"] = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Position,
                            health = 250, weapon = new { ready = true, rounds = 100, range = 15 } },
                        ["enemies"] = Array.Empty<object>()
                    };
                    if (receipt is not null) view["operation"] = receipt.Evidence;
                    data = view;
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Assert.Equal(Map.Scope, submission.Scope);
                    Assert.Contains(submission.Kind, new[] { "move", "wait" });
                    if (submission.Kind == "move")
                    {
                        double tolerance = submission.Args.GetProperty("tolerance").GetDouble();
                        var destination = submission.Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                        double distance = Position.DistanceTo(destination);
                        Assert.True(distance > tolerance, "The native engine must not immediately complete an unstarted movement.");
                        // The real engine stops short, so verify stability there rather than only at the proposed waypoint.
                        Position = new(destination.X + (Position.X - destination.X) * .12 / distance,
                            destination.Y + (Position.Y - destination.Y) * .12 / distance);
                        // Actual positions from the first real 2.0.77 vehicle-corner attempt: native stopping can keep
                        // the perpendicular offset and alternate between two points despite their nominal stability.
                        if (replayNativeStops)
                        {
                            if (destination == new MapPosition(0, .5)) Position = new(.0625, .421875);
                            if (destination == new MapPosition(0, 0)) Position = new(.0625, .125);
                            if (destination == new MapPosition(.5, 0)) Position = new(.421875, .125);
                        }
                    }
                    else Assert.Equal(30, submission.Args.GetProperty("ticks").GetInt32());
                    Submissions.Add(submission);
                    var raw = Protocol.ToElement(new { operationId = submission.OperationId, kind = submission.Kind, status = "completed",
                        acceptedTick = tick, updatedTick = tick, effects = new { position = Position } });
                    receipt = OperationReceipt.Parse(raw, submission.OperationId);
                    data = raw;
                    break;
                case "operation":
                    Assert.Equal(receipt!.OperationId, request.Arguments.GetProperty("operationId").GetString());
                    data = receipt.Evidence;
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
