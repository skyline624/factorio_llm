using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConstructionThreatMarginTests
{
    [Fact]
    public async Task InteractionSelectsAnApproachOutsideTheSameNavigationReserve()
    {
        var placement = new PlacementCandidate(new(18.5, 37.5), 12, 0);
        var map = World(new(27.5, 41));
        var belt = new EntityGeometry("belt", "transport-belt", new(new(-.35, -.35), new(.35, .35)),
            new CollisionMask([], false, false, false), 1, 1, BeltSpeed: .03125);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["belt"] = belt },
            Entities = [new("under-actor", "belt", new(27.5, 41.5), belt.CollisionBox.Translate(new(27.5, 41.5)), 4, "own"),
                new("target", "belt", placement.Position, belt.CollisionBox.Translate(placement.Position), 12, "own")],
            StationaryThreats = [new("worm", new(0, 0), 25, map.CollectedTick)] };
        var old = new PlacementPlanner().FindInteractionApproach(new(map), map.Entities[1]);
        Assert.NotNull(old);
        Assert.False(new SpatialCollisionField(map, ExplorationPlanner.ThreatMargin).Walkable(old));
        var game = new Game(map, placement);
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        await using var controller = new SpatialController(game, new Journal());
        await controller.ApproachEntityAsync("target", placement.Position, catalog);
        Assert.Equal(0, game.Builds);
        Assert.True(game.Moves > 0);
        Assert.True(game.Map.Actor.Position.DistanceTo(placement.Position) <= 9);
        Assert.True(PlacementPlanner.CanStop(new(game.Map, ExplorationPlanner.ThreatMargin), game.Map.Actor.Position));
    }

    [Fact]
    public async Task ConstructionChoosesAnApproachThatNavigationCanReachOutsideTheWormReserve()
    {
        // Synthetic boundary: the nearest place off the existing belt is inside navigation's worm reserve.
        var placement = new PlacementCandidate(new(18.5, 37), 0, 0);
        var map = World(new(27.5, 41));
        var belt = new EntityGeometry("belt", "transport-belt", new(new(-.35, -.35), new(.35, .35)),
            new CollisionMask([], false, false, false), 1, 1);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["belt"] = belt },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["belt"] = new("belt", 100) },
            Entities = [new("previous-belt", "belt", map.Actor.Position, belt.CollisionBox.Translate(map.Actor.Position), 12, "agent")],
            StationaryThreats = [new("worm", new(0, 0), 25, map.CollectedTick)] };
        var navigation = new SpatialCollisionField(map, ExplorationPlanner.ThreatMargin);
        var oldApproach = new PlacementPlanner().FindApproach(new(map), "belt", placement);
        Assert.NotNull(oldApproach);
        Assert.False(navigation.Walkable(oldApproach));
        var game = new Game(map, placement);
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        await using var controller = new SpatialController(game, new Journal());
        string id = await new PoweredMachineController(game, new Journal()).BuildAtAsync("belt", placement, catalog, controller, default);
        Assert.Equal("built", id);
        Assert.Equal(1, game.Builds);
        Assert.True(game.Moves > 0);
        Assert.True(navigation.Walkable(game.Map.Actor.Position));
        Assert.True(PlacementPlanner.CanStop(new(game.Map, ExplorationPlanner.ThreatMargin), game.Map.Actor.Position));
    }

    [Fact]
    public void ConstructionChoosesTheSideWithASafeExitAfterClosingAThreatBoundaryPassage()
    {
        var map = Passage();
        var placement = new PlacementCandidate(new(.5, 56.5), 0, 0);
        var completed = map with {
            Entities = [new("built", "chest", placement.Position, map.Prototypes["chest"].CollisionBox.Translate(placement.Position), 0, "agent")] };
        var approach = new PlacementPlanner().FindApproach(new(map, ExplorationPlanner.ThreatMargin), "chest", placement,
            completedSite: completed);
        Assert.NotNull(approach);
        Assert.True(approach.Y > placement.Position.Y);
        Assert.True(new PlacementPlanner().PreservesExit(new(map with { Actor = map.Actor with { Position = approach } },
            ExplorationPlanner.ThreatMargin), "chest", placement));
    }

    [Fact]
    public void ConstructionCannotCertifyAnExitThatMustCrossTheStationaryThreatReserve()
    {
        var map = Passage();
        var placement = new PlacementCandidate(new(.5, 56.5), 0, 0);
        Assert.True(new PlacementPlanner().PreservesExit(new(map), "chest", placement));
        Assert.False(new PlacementPlanner().PreservesExit(new(map, ExplorationPlanner.ThreatMargin), "chest", placement));
    }

    private static SpatialSnapshot Passage()
    {
        var map = World(new(.5, 55.5));
        // A one-tile grass passage: building north would leave only an exit through the worm's reserve.
        return map with { Rows = Enumerable.Range(-16, 106).SelectMany(y => new[]
            { new TileRun(-32, y, 32, "water"), new TileRun(0, y, 1, "grass"), new TileRun(1, y, 64, "water") }).ToArray(),
            StationaryThreats = [new("worm", new(.5, 0), 25, map.CollectedTick)] };
    }

    private static SpatialSnapshot World(MapPosition actor)
    {
        var map = SpatialPlannerTests.Map([]);
        return map with { Actor = map.Actor with { Position = actor }, Bounds = new(new(-32, -16), new(65, 90)),
            Rows = Enumerable.Range(-16, 106).Select(y => new TileRun(-32, y, 97, "grass")).ToArray(),
            Coverage = map.Coverage with { Radius = 48 } };
    }

    private sealed class Game(SpatialSnapshot initial, PlacementCandidate placement) : IGameClient
    {
        public SpatialSnapshot Map { get; private set; } = initial;
        public int Builds { get; private set; }
        public int Moves { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object data = request.Action switch
            {
                "spatial" => Map,
                "observe" => new { Map.Scope, collectedTick = Map.CollectedTick,
                    coverage = new { atomic = true, collectionStartTick = Map.CollectedTick, collectionEndTick = Map.CollectedTick,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>() },
                "validate_placement" => new PlacementValidation(Map.Scope, Map.CollectedTick, "belt", [new(1, placement.Position, 0, true, true)]),
                "submit" => Submit(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Map.CollectedTick, Protocol.ToElement(data)));
        }
        private object Submit(OperationSubmission operation)
        {
            object effects;
            if (operation.Kind == "move")
            {
                var point = operation.Args.TryGetProperty("position", out var position) ? position : operation.Args.GetProperty("destination");
                var destination = point.Deserialize<MapPosition>(Protocol.Json)!;
                Assert.True(new SpatialCollisionField(Map, ExplorationPlanner.ThreatMargin).SegmentClear(Map.Actor.Position, destination, 0));
                Map = Map with { Actor = Map.Actor with { Position = destination } };
                Moves++;
                effects = new { position = destination };
            }
            else
            {
                Assert.Equal("build", operation.Kind);
                Assert.True(PlacementPlanner.CanStop(new(Map, ExplorationPlanner.ThreatMargin), Map.Actor.Position));
                Builds++;
                effects = new { entityId = "built", inventoryDelta = new Dictionary<string, int> { ["belt"] = -1 } };
            }
            long tick = Map.CollectedTick + 1;
            Map = Map with { CollectedTick = tick,
                StationaryThreats = Map.StationaryThreats?.Select(t => t with { CollectedTick = tick }).ToArray() };
            return new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = Map.CollectedTick,
                updatedTick = Map.CollectedTick, effects };
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
}
