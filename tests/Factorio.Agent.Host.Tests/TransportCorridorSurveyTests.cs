using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TransportCorridorSurveyTests
{
    [Fact]
    public async Task ALongSurveyWalksAroundALakeInsteadOfRequestingItsInterpolatedCentre()
    {
        var game = new SurveyGame();
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var map = await new TransportCorridorSurvey(game, journal).CaptureAsync(game.Factory, ["source", "target"],
            ["belt"], game.Catalog, controller, CancellationToken.None);

        Assert.NotNull(map);
        Assert.Contains(game.Movements, p => Math.Abs(p.Y) > 12);
        Assert.Contains(map.Entities, e => e.Id == "source");
        Assert.Contains(map.Entities, e => e.Id == "target");
        Assert.InRange(game.Photos.Count, 3, TransportCorridorSurvey.MaximumSamples);
        Assert.All(game.Photos, p => Assert.Equal(48, p.Coverage.Radius));
        Assert.False(map.Coverage.Atomic);
        Assert.False(map.Coverage.Complete);
        Assert.Contains("factory-transport-corridor", journal.Types);
        Assert.DoesNotContain("mine", game.Kinds);
        var belt = new BeltRoutePlanner().Find(map, "belt", new(-1.5, .5), new(178.5, .5), nodeBudget: 100000);
        Assert.Equal(BeltRouteStatus.Found, belt.Status);
        Assert.All(belt.Belts, b => Assert.False(SurveyGame.InLake(b.Position)));
    }

    [Fact]
    public async Task ALongerActualTripStopsAtItsSampleBudgetWithoutCertifyingTheEndpoints()
    {
        var game = new SurveyGame(length: 840, lakeHalfHeight: 32);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        var map = await new TransportCorridorSurvey(game, journal).CaptureAsync(game.Factory, ["source", "target"],
            ["belt"], game.Catalog, controller, CancellationToken.None);
        Assert.Null(map);
        Assert.InRange(game.Photos.Count, 1, TransportCorridorSurvey.MaximumSamples - 1);
        Assert.Contains("factory-transport-corridor-limited", journal.Types);
        Assert.DoesNotContain("factory-transport-corridor", journal.Types);
        Assert.True(game.Position.DistanceTo(new(840.5, .5)) > 24);
    }

    [Fact]
    public async Task CancellationDuringACompletedStepsPhotoDoesNotStartAnotherMove()
    {
        using var cancellation = new CancellationTokenSource();
        var game = new SurveyGame(interruption: cancellation);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TransportCorridorSurvey(game, journal).CaptureAsync(game.Factory, ["source", "target"],
                ["belt"], game.Catalog, controller, cancellation.Token));
        Assert.Equal(2, game.Photos.Count);
        Assert.True(game.Movements.Count > 0);
        Assert.DoesNotContain("factory-transport-corridor", journal.Types);
    }

    [Fact]
    public async Task AChangedIncarnationCannotJoinTheOldCorridor()
    {
        var game = new SurveyGame(changeScope: true);
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TransportCorridorSurvey(game, journal).CaptureAsync(game.Factory, ["source", "target"],
                ["belt"], game.Catalog, controller, CancellationToken.None));
        Assert.Equal(2, game.Photos.Count);
        Assert.DoesNotContain("factory-transport-corridor", journal.Types);
    }

    // Synthetic engine: only local photos, an impassable lake, two own chests and confirmed simulated moves.
    // This verifies surveying behavior, not a normal game, native construction or production.
    private sealed class SurveyGame(int length = 180, CancellationTokenSource? interruption = null, bool changeScope = false, int lakeHalfHeight = 12) : IGameClient
    {
        private readonly SpatialSnapshot basis = BeltRoutePlannerTests.Map();
        private MapPosition position = new(-2, .5);
        private long tick = 100;
        public List<SpatialSnapshot> Photos { get; } = [];
        public List<MapPosition> Movements { get; } = [];
        public List<string> Kinds { get; } = [];
        public MapPosition Position => position;
        public ProductionCatalog Catalog => new(basis.Scope, 100, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        public FactorySnapshot Factory => new("synthetic", basis.Scope, 100, 3700, Protocol.ToElement(new { }),
            Endpoints.Select(e => new FactoryRecord(e.Id, "entity", e.Id, e.Name, Protocol.ToElement(new { e.Position }))).ToArray());
        private SpatialEntity[] Endpoints => new[] { ("source", new MapPosition(.5, .5)), ("target", new MapPosition(length + .5, .5)) }
            .Select(e => new SpatialEntity(e.Item1, "chest", e.Item2, basis.Prototypes["chest"].CollisionBox.Translate(e.Item2), 0, "own")).ToArray();
        internal static bool InLake(MapPosition p, int halfHeight = 12) => p.X is >= 30 and < 150 && p.Y >= -halfHeight && p.Y < halfHeight;

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tick++;
            object data;
            switch (request.Action)
            {
                case "spatial":
                    int radius = request.Arguments.GetProperty("radius").GetInt32();
                    Assert.InRange(radius, 4, 48);
                    var photo = Map(radius);
                    if (request.Arguments.GetProperty("items").GetArrayLength() > 0)
                    {
                        Photos.Add(photo);
                        if (Photos.Count == 2 && interruption is not null)
                        {
                            interruption.Cancel();
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (Photos.Count == 2 && changeScope)
                            photo = photo with { Scope = photo.Scope with { Incarnation = photo.Scope.Incarnation + 1 } };
                    }
                    data = photo;
                    break;
                case "observe":
                    data = new
                    {
                        basis.Scope, collectedTick = tick,
                        coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>()
                    };
                    break;
                case "submit":
                    var operation = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Kinds.Add(operation.Kind);
                    Assert.Equal(basis.Scope, operation.Scope);
                    if (operation.Kind == "move")
                    {
                        Assert.Equal(position, operation.Preconditions.GetProperty("position").Deserialize<MapPosition>(Protocol.Json));
                        var points = operation.Args.TryGetProperty("waypoints", out var path) && path.ValueKind == JsonValueKind.Array
                            ? path.Deserialize<MapPosition[]>(Protocol.Json)!
                            : [operation.Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!];
                        var field = new SpatialCollisionField(Map(48));
                        double distance = 0;
                        foreach (var point in points)
                        {
                            Assert.True(field.SegmentClear(position, point, 0));
                            distance += position.DistanceTo(point);
                            position = point;
                            Movements.Add(point);
                        }
                        Assert.InRange(distance, 0, 24.000001);
                    }
                    else Assert.Equal("wait", operation.Kind);
                    data = new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = tick, updatedTick = tick, effects = new { } };
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }

        private SpatialSnapshot Map(int radius)
        {
            int left = (int)Math.Floor(position.X) - radius, top = (int)Math.Floor(position.Y) - radius;
            var bounds = new WorldBox(new(left, top), new(left + 2 * radius + 1, top + 2 * radius + 1));
            var rows = new List<TileRun>();
            for (int y = top; y < bounds.Max.Y; y++)
            {
                int start = left;
                string name = Tile(left);
                for (int x = left + 1; x <= bounds.Max.X; x++)
                    if (x == bounds.Max.X || Tile(x) != name)
                    {
                        rows.Add(new(start, y, x - start, name));
                        start = x;
                        name = Tile(x);
                    }
                string Tile(int x) => InLake(new(x, y), lakeHalfHeight) ? "water" : "grass";
            }
            return basis with { CollectedTick = tick, Bounds = bounds, Rows = rows, Actor = basis.Actor with { Position = position },
                Coverage = new(true, true, "current-character-local-area", radius), Entities = Endpoints.Where(e => bounds.Contains(e.Position)).ToArray() };
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token) { token.ThrowIfCancellationRequested(); Types.Add(type); return Task.CompletedTask; }
    }
}
