using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryMaintenanceScopeTests
{
    [Fact]
    public async Task ALocalRoundCannotDetourToAStockedRepairOutsideItsCellSet()
    {
        using var world = new World();
        var local = Cell("local-wall", "wall", "stone-wall", "gone-local", new(2.5, 2.5));
        var remote = Cell("remote-turret", "turret", "gun-turret", "gone-remote", new(80, 56));
        await world.Registry.SaveAsync(new(1, world.Catalog.Scope.WorldId, [], [local, remote]), default);
        world.Game.Carried["gun-turret"] = 1;

        var result = await world.RunAsync(new HashSet<string> { local.Id });

        Assert.Equal(1, result.Shortfall["stone-wall"]);
        Assert.DoesNotContain("gun-turret", result.Shortfall.Keys);
        Assert.Equal(0, result.Actions);
        Assert.DoesNotContain("spatial", world.Game.Calls);
        Assert.Contains("factory-maintenance-scope", world.Journal.Types);
        var retained = (await world.Registry.LoadAsync(world.Catalog.Scope.WorldId, default)).Cells.Single(c => c.Id == remote.Id);
        Assert.Equal(remote.Entities, retained.Entities);
        Assert.Equal(remote.Plan, retained.Plan);
        // The ordinary round still owns the remote repair and attempts its native approach.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => world.RunAsync());
        Assert.Equal("Unexpected request: spatial", error.Message);
    }

    [Fact]
    public async Task ALocalRoundCannotSpendItsMagazinesRearmingARemoteTurret()
    {
        using var world = new World();
        var local = Cell("local-turret", "turret", "gun-turret", "local", new(2, 2));
        var remote = Cell("remote-turret", "turret", "gun-turret", "remote", new(80, 56));
        await world.Registry.SaveAsync(new(1, world.Catalog.Scope.WorldId, [], [local, remote]), default);
        world.Game.Carried["firearm-magazine"] = 20;
        world.Game.Entities.AddRange(Turret("local", local.Plan!["turret"].Position, 100));
        world.Game.Entities.AddRange(Turret("remote", remote.Plan!["turret"].Position, 0));

        var result = await world.RunAsync(new HashSet<string> { local.Id });

        Assert.Empty(result.Shortfall);
        Assert.Empty(result.Supplied);
        Assert.Equal(0, result.Actions);
        Assert.DoesNotContain("spatial", world.Game.Calls);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => world.RunAsync());
        Assert.Equal("Unexpected request: spatial", error.Message);
    }

    [Fact]
    public async Task AnEmptySelectionDoesNotBecomeAWholeFactoryRound()
    {
        using var world = new World();
        await world.Registry.SaveAsync(new(1, world.Catalog.Scope.WorldId, [],
            [Cell("remote", "turret", "gun-turret", "gone", new(80, 56))]), default);
        world.Game.Carried["gun-turret"] = 1;

        var result = await world.RunAsync(new HashSet<string>());

        Assert.Empty(result.Shortfall);
        Assert.Empty(result.Rebuilt);
        Assert.DoesNotContain("spatial", world.Game.Calls);
    }

    [Fact]
    public async Task CellSelectionCannotHideAnActorScopeChange()
    {
        using var world = new World();
        world.Game.Scope = world.Catalog.Scope with { Incarnation = 2 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => world.RunAsync(new HashSet<string>()));
        Assert.Contains("Actor identity changed", error.Message);
    }

    private static FactoryCell Cell(string id, string kind, string item, string entityId, MapPosition position) =>
        new(id, 0, new(0, 0, true), kind, item, null, new Dictionary<string, string> { [kind] = entityId }, "ready", 1,
            Plan: new Dictionary<string, PlannedEntity> { [kind] = new(kind, item, position, 0) });

    private static FactoryRecord[] Turret(string id, MapPosition position, long rounds) =>
    [
        new(id, "entity", id, "gun-turret", Protocol.ToElement(new
        {
            role = "factory", type = "ammo-turret", surfaceIndex = 1, position, direction = 0, quality = "normal",
            active = true, ammoInventoryId = id + "-ammo", ammoRounds = rounds, defenseReady = rounds > 0, defenseRange = 18
        })),
        new(id + "-ammo", "inventory", id, "gun-turret", Protocol.ToElement(new
        {
            items = rounds > 0 ? new Dictionary<string, long> { ["firearm-magazine"] = 10 } : new Dictionary<string, long>()
        }))
    ];

    private sealed class World : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("maintenance-scope-").FullName;
        public FactoryRegistry Registry { get; }
        public Journal Journal { get; } = new();
        public Game Game { get; } = new();
        public ProductionCatalog Catalog { get; } = Catalogs.Early() with
        {
            Items = new Dictionary<string, NativeItem>
            {
                ["stone-wall"] = new(0, 100, PlaceEntity: "stone-wall", PlaceEntityType: "wall"),
                ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret"),
                ["firearm-magazine"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 5)
            },
            Turrets = new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) }
        };

        public World() => Registry = new(directory);
        public async Task<MaintenanceResult> RunAsync(IReadOnlySet<string>? targetCellIds = null)
        {
            await using var controller = new SpatialController(Game, Journal);
            return await new FactoryMaintenance(Game, Journal, directory).RunAsync(controller, Catalog, default, targetCellIds);
        }
        public void Dispose() => Directory.Delete(directory, true);
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    private sealed class Game : IGameClient
    {
        public ActorScope Scope { get; set; } = Catalogs.Early().Scope;
        public Dictionary<string, long> Carried { get; } = [];
        public List<FactoryRecord> Entities { get; } = [];
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            if (request.Action != "factory_snapshot") throw new InvalidOperationException($"Unexpected request: {request.Action}");
            FactoryRecord[] records =
            [
                new("actor", "entity", "actor", "character", Protocol.ToElement(new
                    { role = "actor", surfaceIndex = 1, type = "character", position = new MapPosition(0, 0), mainInventoryId = "main" })),
                new("main", "inventory", "actor", "character", Protocol.ToElement(new { items = Carried })),
                .. Entities
            ];
            var data = new
            {
                snapshotId = "snapshot-" + Calls.Count, scope = Scope, snapshotScope = Scope, collectedTick = 10, expiresTick = 100,
                totalRecords = records.Length, offset = 0, nextOffset = records.Length, complete = true,
                coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }, records
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 10, Protocol.ToElement(data)));
        }
    }
}
