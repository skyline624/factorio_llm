using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NavigationObservationTests
{
    [Fact]
    public async Task ADetourOutsideTheSmallViewIsWalkedWithoutRemovingItsBarrier()
    {
        var game = new CorridorGame();
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var result = await controller.NavigateAsync(new(4.5, .5));

        Assert.InRange(result.Position.DistanceTo(new(4.5, .5)), 0, .4);
        Assert.Contains(game.Movements, p => Math.Abs(p.Y) > 35);
        Assert.Single(journal.Types, t => t == "route-observation-expanded");
        Assert.DoesNotContain("mine", game.SubmittedKinds);
        Assert.Equal(0, game.CatalogReads);
        Assert.True(game.Radii.Count(r => r == 48) >= 3);
        int expanded = journal.RouteRadii.IndexOf(48);
        Assert.True(expanded >= 0);
        Assert.All(journal.RouteRadii.Skip(expanded), radius => Assert.Equal(48, radius));
    }

    [Fact]
    public async Task ABarrierAcrossBothViewsStillRefusesMovementAfterOneExpansion()
    {
        var game = new CorridorGame(blockBothViews: true);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var error = await Assert.ThrowsAsync<NavigationPlanningException>(() => controller.NavigateAsync(new(4.5, .5)));

        Assert.Equal(RouteStatus.NoRouteOnKnownGrid, error.Status);
        Assert.Empty(game.Movements);
        Assert.Single(game.Radii, r => r == 48);
        Assert.Single(journal.Types, t => t == "route-observation-expanded");
        Assert.Equal(1, game.CatalogReads);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("manual")]
    [InlineData("older")]
    public async Task AChangedOrOlderWiderViewCannotDispatchTheOldIntent(string change)
    {
        var game = new CorridorGame(change: change);
        await using var controller = new SpatialController(game, new Journal());
        if (change == "manual")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.NavigateAsync(new(4.5, .5)));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => controller.NavigateAsync(new(4.5, .5)));
        Assert.Empty(game.Movements);
        Assert.Empty(game.SubmittedAfterChangedView);
        Assert.Single(game.Radii, radius => radius == 48);
        Assert.Equal(0, game.CatalogReads);
    }

    [Fact]
    public async Task CancellationDuringTheWiderObservationCannotStartMovement()
    {
        using var cancellation = new CancellationTokenSource();
        var game = new CorridorGame(interruption: cancellation);
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.NavigateAsync(new(4.5, .5), cancellationToken: cancellation.Token));
        Assert.Empty(game.Movements);
    }

    [Fact]
    public async Task ADirectClearRouteDoesNotRequestTheLargerView()
    {
        var game = new CorridorGame();
        await using var controller = new SpatialController(game, new Journal());
        var result = await controller.NavigateAsync(new(-4.5, 4.5));
        Assert.InRange(result.Position.DistanceTo(new(-4.5, 4.5)), 0, .4);
        Assert.DoesNotContain(48, game.Radii);
    }

    [Fact]
    public async Task ALostMoveReplyOnTheDetourQueriesItsIdentityInsteadOfRepeatingTheMove()
    {
        var game = new CorridorGame(loseFirstMoveReply: true);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var result = await controller.NavigateAsync(new(4.5, .5));
        Assert.InRange(result.Position.DistanceTo(new(4.5, .5)), 0, .4);
        Assert.Single(journal.Types, type => type == "outcome-unknown");
        Assert.Equal(1, game.ReceiptQueries);
        Assert.Equal(result.Receipts.Count, game.SubmittedKinds.Count(kind => kind == "move"));
        Assert.DoesNotContain("mine", game.SubmittedKinds);
    }

    // Synthetic narrow ground with a tall barrier: the exit at y=36 is invisible in radius 32.
    // The fake engine checks every submitted leg and changes position only after a completed move.
    private sealed class CorridorGame(bool blockBothViews = false, string? change = null,
        CancellationTokenSource? interruption = null, bool loseFirstMoveReply = false) : IGameClient
    {
        private MapPosition position = new(-4.5, .5);
        private readonly long tick = 100;
        private object? lastReceipt;
        private string? lastOperationId;
        private bool replyLost;
        private bool changedViewDelivered;
        public List<int> Radii { get; } = [];
        public List<string> SubmittedKinds { get; } = [];
        public List<string> SubmittedAfterChangedView { get; } = [];
        public List<MapPosition> Movements { get; } = [];
        public int CatalogReads { get; private set; }
        public int ReceiptQueries { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object data;
            long responseTick = tick;
            switch (request.Action)
            {
                case "spatial":
                    int radius = request.Arguments.GetProperty("radius").GetInt32();
                    Radii.Add(radius);
                    if (radius == 48 && interruption is not null)
                    {
                        interruption.Cancel();
                        throw new OperationCanceledException(interruption.Token);
                    }
                    var photograph = Map(radius);
                    if (radius == 48)
                        photograph = change switch
                        {
                            "scope" => photograph with { Scope = photograph.Scope with { Generation = photograph.Scope.Generation + 1 } },
                            "manual" => photograph with { Actor = photograph.Actor with { ControlMode = "manual" } },
                            "older" => photograph with { CollectedTick = tick - 1 },
                            _ => photograph
                        };
                    responseTick = photograph.CollectedTick;
                    if (radius == 48 && change is not null) changedViewDelivered = true;
                    data = photograph;
                    break;
                case "observe":
                    var map = Map(32);
                    data = new
                    {
                        map.Scope, collectedTick = tick,
                        coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                        enemies = Array.Empty<object>()
                    };
                    break;
                case "submit":
                    var operation = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    SubmittedKinds.Add(operation.Kind);
                    if (changedViewDelivered) SubmittedAfterChangedView.Add(operation.Kind);
                    if (operation.Kind == "move")
                    {
                        Assert.Equal(position, operation.Preconditions.GetProperty("position").Deserialize<MapPosition>(Protocol.Json));
                        var points = operation.Args.TryGetProperty("waypoints", out var path) && path.ValueKind == JsonValueKind.Array
                            ? path.Deserialize<MapPosition[]>(Protocol.Json)!
                            : [operation.Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!];
                        var field = new SpatialCollisionField(Map(48));
                        double length = 0;
                        foreach (var point in points)
                        {
                            Assert.True(field.SegmentClear(position, point, 0));
                            length += position.DistanceTo(point);
                            position = point;
                            Movements.Add(point);
                        }
                        Assert.InRange(length, 0, 24.000001);
                    }
                    else Assert.Equal("wait", operation.Kind);
                    data = new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = tick,
                        updatedTick = tick, effects = new { } };
                    lastReceipt = data;
                    lastOperationId = operation.OperationId;
                    if (loseFirstMoveReply && operation.Kind == "move" && !replyLost)
                    {
                        replyLost = true;
                        throw new IOException("Move completed, reply lost.");
                    }
                    break;
                case "operation":
                    ReceiptQueries++;
                    Assert.Equal(lastOperationId, request.Arguments.GetProperty("operationId").GetString());
                    data = lastReceipt ?? throw new InvalidOperationException("No accepted operation to reconcile.");
                    break;
                case "production_catalog":
                    CatalogReads++;
                    data = new ProductionCatalog(Map(48).Scope, tick, [], new Dictionary<string, NativeItem>(),
                        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, responseTick, Protocol.ToElement(data)));
        }

        private SpatialSnapshot Map(int radius)
        {
            var map = SpatialPlannerTests.Map([]);
            int x = (int)Math.Floor(position.X) - radius, y = (int)Math.Floor(position.Y) - radius;
            var rows = new List<TileRun>();
            for (int row = y; row <= y + radius * 2; row++)
            {
                int start = x;
                string name = Tile(x);
                for (int column = x + 1; column <= x + radius * 2 + 1; column++)
                    if (column == x + radius * 2 + 1 || Tile(column) != name)
                    {
                        rows.Add(new(start, row, column - start, name));
                        start = column;
                        name = Tile(column);
                    }
                // Observation guards need the bounded wider read, not thousands of nodes of detour search.
                // Keep the long navigable corridor for the route tests; disconnected narrow strips make
                // the changed/manual/older/cancelled read deterministic before any actor submission.
                string Tile(int column) => (change is not null || interruption is not null
                    ? column is >= -5 and <= -4 or >= 4 and <= 5
                    : column is >= -8 and <= 8) ? "grass" : "water";
            }
            double halfHeight = blockBothViews ? 60 : 35;
            return map with
            {
                Bounds = new(new(x, y), new(x + radius * 2 + 1, y + radius * 2 + 1)),
                Actor = map.Actor with { Position = position },
                Coverage = map.Coverage with { Radius = radius },
                Rows = rows,
                Entities = [new("barrier", "wall", new(0, 0), new(new(-.5, -halfHeight), new(.5, halfHeight)), 0, "own")]
            };
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public List<int> RouteRadii { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Types.Add(type);
            if (type == "route-plan") RouteRadii.Add(Protocol.ToElement(data).GetProperty("radius").GetInt32());
            return Task.CompletedTask;
        }
    }
}
