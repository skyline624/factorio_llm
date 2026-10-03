using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConstructionTreeClearanceTests
{
    private static readonly PlacementCandidate Site = new(new(.5, .5), 0, 0);

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1d / 256, false)]
    [InlineData(1d / 256, true)]
    public void NativeQuantizedTreeContactBlocksConstructionButOneUnitOfSeparationDoesNot(double gap, bool allowed)
    {
        var (map, catalog) = World(gap);
        var field = new SpatialCollisionField(map);
        Assert.Equal(allowed, field.PlacementClear(map.Prototypes["pipe"], Site.Position, 0));
        Assert.Equal(allowed ? null : "tree", new TreeClearancePlanner().SelectPlacement(map, catalog, "pipe", Site)?.Id);
        // A body already touching the obstacle can still escape; construction has a different contact rule.
        if (gap == 0) Assert.False(new OrientedCollisionBox(map.Entities.Single().Bounds)
            .Overlaps(map.Prototypes["pipe"].CollisionBox.Translate(Site.Position)));
    }

    [Theory]
    [InlineData("owned-tree")]
    [InlineData("resource")]
    [InlineData("no-solid-product")]
    [InlineData("building")]
    [InlineData("water")]
    public void ARefusedFootprintCannotClearResourcesBuildingsOwnedTreesOrInvalidTerrain(string obstruction)
    {
        var (map, catalog) = World();
        if (obstruction == "owned-tree") map = map with { Entities = [map.Entities.Single() with { Force = "agent" }] };
        if (obstruction == "resource") map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["tree"] = map.Prototypes["tree"] with { Type = "resource" } } };
        if (obstruction == "no-solid-product") catalog = catalog with { Mining = new Dictionary<string, NativeMaterial[]>() };
        if (obstruction == "building") map = map with { Entities = [.. map.Entities, new("building", "wall", Site.Position,
            new(new(.2, .2), new(.8, .8)), 0, "agent")] };
        if (obstruction == "water") map = map with { Rows = map.Rows.Select(r => r.Y == 0 ? r with { Name = "water" } : r).ToArray() };
        Assert.Null(new TreeClearancePlanner().SelectPlacement(map, catalog, "pipe", Site));
    }

    [Fact]
    public void AChangedActorCannotAuthorizeTreeClearance()
    {
        var (map, catalog) = World();
        Assert.Throws<InvalidDataException>(() => new TreeClearancePlanner().SelectPlacement(
            map with { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } }, catalog, "pipe", Site));
    }

    [Fact]
    public async Task CommittedConstructionMinesOneProvenTreeThenRevalidatesBeforeBuilding()
    {
        var game = new Game();
        var journal = new Journal();
        await using var controller = new SpatialController(game, journal);
        string built = await new PoweredMachineController(game, journal).BuildAtAsync("pipe", Site, game.Catalog, controller, default);
        Assert.Equal("built", built);
        Assert.Equal(["mine", "build"], game.Submissions.Where(k => k != "move"));
        Assert.Equal(2, game.Validations);
        Assert.Contains("construction-tree-cleared", journal.Events);
    }

    [Fact]
    public async Task AnUnprovenTreeRemovalNeverBuildsOrRepeatsMining()
    {
        var game = new Game { MineStatus = "partial" };
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PoweredMachineController(game, new Journal())
            .BuildAtAsync("pipe", Site, game.Catalog, controller, default));
        Assert.Equal(["mine"], game.Submissions.Where(k => k != "move"));
        Assert.Equal(1, game.Validations);
    }

    [Fact]
    public async Task AnUnknownClearanceObservationStopsConstructionWithoutRepeatingTheCompletedMutation()
    {
        var game = new Game { UnknownAfterMining = true };
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => new PoweredMachineController(game, new Journal())
            .BuildAtAsync("pipe", Site, game.Catalog, controller, default));
        Assert.Equal(["mine"], game.Submissions.Where(k => k != "move"));
    }

    private static (SpatialSnapshot Map, ProductionCatalog Catalog) World(double gap = 0)
    {
        var map = SpatialPlannerTests.Map([]);
        var mask = map.Prototypes["wall"].Mask;
        var tree = new EntityGeometry("tree", "tree", new(new(-.3984375, -.3984375), new(.3984375, .3984375)), mask, 1, 1);
        var pipe = new EntityGeometry("pipe", "pipe", new(new(-.2890625, -.2890625), new(.2890625, .2890625)), mask, 1, 1);
        var at = new MapPosition(.8125, 1.1875 + gap);
        map = map with { Actor = map.Actor with { Position = new(-1.5, .5) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["tree"] = tree, ["pipe"] = pipe },
            Entities = [new("tree", "tree", at, tree.CollisionBox.Translate(at), 0, "neutral")],
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pipe"] = new("pipe", 100) } };
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]> { ["tree"] = [new("wood", "item", 4)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        return (map, catalog);
    }

    private sealed class Game : IGameClient
    {
        public SpatialSnapshot Map { get; private set; } = World().Map;
        public ProductionCatalog Catalog { get; } = World().Catalog;
        public List<string> Submissions { get; } = [];
        public int Validations { get; private set; }
        public string MineStatus { get; init; } = "completed";
        public bool UnknownAfterMining { get; init; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Action == "spatial" && UnknownAfterMining && Submissions.Contains("mine"))
                throw new OperationOutcomeUnknownException("tree-operation", new IOException("missing observation"));
            object data = request.Action switch
            {
                "spatial" => Map,
                "observe" => new { Map.Scope, collectedTick = Map.CollectedTick,
                    coverage = new { atomic = true, collectionStartTick = Map.CollectedTick, collectionEndTick = Map.CollectedTick,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>() },
                "validate_placement" => Validate(),
                "submit" => Submit(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Map.CollectedTick, Protocol.ToElement(data)));
        }
        private object Validate()
        {
            Validations++;
            return new PlacementValidation(Map.Scope, Map.CollectedTick, "pipe", [new(1, Site.Position, 0, Map.Entities.Count == 0, true)]);
        }
        private object Submit(OperationSubmission operation)
        {
            Submissions.Add(operation.Kind);
            object effects;
            if (operation.Kind == "move")
            {
                Map = Map with { Actor = Map.Actor with { Position = operation.Args.GetProperty("destination").Deserialize<MapPosition>(Protocol.Json)! } };
                effects = new { position = Map.Actor.Position };
            }
            else if (operation.Kind == "mine") { Map = Map with { Entities = [] }; effects = new { targetId = "tree", produced = 4 }; }
            else { Assert.Equal("build", operation.Kind); effects = new { entityId = "built" }; }
            Map = Map with { CollectedTick = Map.CollectedTick + 1 };
            return new { operation.OperationId, operation.Kind, status = operation.Kind == "mine" ? MineStatus : "completed",
                acceptedTick = Map.CollectedTick, updatedTick = Map.CollectedTick, effects };
        }
    }
    private sealed class Journal : IControllerJournal
    {
        public List<string> Events { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token) { Events.Add(type); return Task.CompletedTask; }
    }
}
