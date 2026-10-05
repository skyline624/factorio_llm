using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PortableDefenseTests
{
    [Fact]
    public void ExposedPackGetsAReachableTurretWithoutLeavingTheActorsPositionOrSpendingItsMagazineReserve()
    {
        var (map, catalog, observed, carried) = Fixture();
        var result = new PortableDefensePlanner().Find(observed, map, catalog, carried)!;
        Assert.Equal("native-turret", result.Turret);
        Assert.Equal("basic-rounds", result.Ammunition);
        Assert.Equal(20, result.Magazines);
        Assert.True(carried[result.Ammunition] - result.Magazines >= SurvivalKitPlanner.MagazineReserve);
        Assert.True(result.Placement.Position.X > observed.Position!.X);
        Assert.True(result.Placement.Position.DistanceTo(observed.Position) <= 4);
        var field = new SpatialCollisionField(map);
        Assert.True(field.PlacementClear(map.Prototypes[map.Items[result.Turret].EntityName], result.Placement.Position, result.Placement.Direction));
        Assert.True(new PlacementPlanner().PreservesExit(field, result.Turret, result.Placement));
    }

    [Fact]
    public void ASecondPaidTurretCanReinforceOneLoadedRefugeWithoutLeavingIt()
    {
        var (map, catalog, observed, carried) = Fixture();
        var first = new PortableDefensePlanner().Find(observed, map, catalog, carried)!;
        map = map with { Entities = [new("first", "turret-entity", first.Placement.Position,
            new(new(first.Placement.Position.X - .7, first.Placement.Position.Y - .7),
                new(first.Placement.Position.X + .7, first.Placement.Position.Y + .7)), first.Placement.Direction, "agent")] };
        observed = observed with { Defenses = [new("first", first.Placement.Position, 18, 200, 100)] };
        carried["native-turret"] = 1; carried["basic-rounds"] = 40;
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog, carried));
        var second = new PortableDefensePlanner().Find(observed, map, catalog, carried, requiredCovers: 2)!;
        Assert.NotEqual(first.Placement.Position, second.Placement.Position);
        Assert.True(second.Placement.Position.DistanceTo(observed.Position!) <= 4);
        Assert.Equal(20, carried[second.Ammunition] - second.Magazines);
        observed = observed with { Defenses = [.. observed.Defenses!, new("second", second.Placement.Position, 18, 200, 100)] };
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog, carried, requiredCovers: 2));
    }

    [Fact]
    public void ARefusedFootprintIsExcludedFromAFreshPaidPlacement()
    {
        var (map, catalog, observed, carried) = Fixture();
        var planner = new PortableDefensePlanner();
        var refused = planner.Find(observed, map, catalog, carried)!.Placement.Position;
        var replacement = planner.Find(observed, map, catalog, carried, refusedPlacements: [refused])!;
        Assert.True(replacement.Placement.Position.DistanceTo(refused) >= 1.5);
        Assert.True(replacement.Placement.Position.DistanceTo(observed.Position!) <= 4);
        Assert.Equal(2, carried[replacement.Turret]);
        Assert.Equal(60, carried[replacement.Ammunition]);
        Assert.True(new PlacementPlanner().PreservesExit(new(map), replacement.Turret, replacement.Placement));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("stopped")]
    [InlineData("dead")]
    [InlineData("zero-health")]
    [InlineData("truncated-enemies")]
    [InlineData("two-enemies")]
    [InlineData("no-enemies")]
    [InlineData("already-covered")]
    [InlineData("map-old")]
    [InlineData("map-late")]
    [InlineData("actor-moved")]
    [InlineData("map-manual")]
    [InlineData("unknown-types")]
    [InlineData("stationary-enemies")]
    [InlineData("only-two-mobile")]
    public void UnsafeOrUnneededViewsCannotChooseConstruction(string condition)
    {
        var (map, catalog, observed, carried) = Fixture();
        switch (condition)
        {
            case "manual": observed = observed with { ControlMode = "manual" }; break;
            case "stopped": observed = observed with { StopUnconfirmed = true }; break;
            case "dead": observed = observed with { Alive = false, Position = null }; break;
            case "zero-health": observed = observed with { Health = 0 }; break;
            case "truncated-enemies": observed = observed with { LocalEnemiesComplete = false }; break;
            case "two-enemies": observed = observed with { Enemies = observed.Enemies.Take(2).ToArray() }; break;
            case "no-enemies": observed = observed with { Enemies = [] }; break;
            case "already-covered": observed = observed with { Defenses = [new("loaded", new(-3, 0), 18, 100, 100)] }; break;
            case "map-old": map = map with { CollectedTick = 99 }; break;
            case "map-late": map = map with { CollectedTick = 161 }; break;
            case "actor-moved": map = map with { Actor = map.Actor with { Position = new(1, 0) } }; break;
            case "map-manual": map = map with { Actor = map.Actor with { ControlMode = "manual" } }; break;
            case "unknown-types": observed = observed with { Enemies = observed.Enemies.Select(e => e with { Type = null }).ToArray() }; break;
            case "stationary-enemies": observed = observed with { Enemies = observed.Enemies.Select(e => e with { Type = "unit-spawner" }).ToArray() }; break;
            case "only-two-mobile": observed = observed with { Enemies = observed.Enemies.Select((e, i) => e with { Type = i == 0 ? "turret" : "unit" }).ToArray() }; break;
        }
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog, carried));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(2, 0)]
    [InlineData(2, 19)]
    [InlineData(2, 39)]
    public void ACarriedTurretAndPaidAmmunitionBeyondTheGunReserveAreBothRequired(long turrets, long magazines)
    {
        var (map, catalog, observed, _) = Fixture();
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog,
            new Dictionary<string, long> { ["native-turret"] = turrets, ["basic-rounds"] = magazines }));
    }

    [Fact]
    public void StrongerCompatibleRoundsAreUsedOnlyWhenTheirGunReserveSurvives()
    {
        var (map, catalog, observed, carried) = Fixture();
        carried["strong-rounds"] = 40;
        carried["incompatible-rounds"] = 100;
        Assert.Equal("strong-rounds", new PortableDefensePlanner().Find(observed, map, catalog, carried)!.Ammunition);
        carried["strong-rounds"] = 39;
        Assert.Equal("basic-rounds", new PortableDefensePlanner().Find(observed, map, catalog, carried)!.Ammunition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DifferentCatalogOrSafetyActorRequiresReconciliation(bool safety)
    {
        var (map, catalog, observed, carried) = Fixture();
        if (safety) observed = observed with { Scope = observed.Scope with { Incarnation = 2 } };
        else catalog = catalog with { Scope = catalog.Scope with { Generation = 3 } };
        Assert.Throws<InvalidDataException>(() => new PortableDefensePlanner().Find(observed, map, catalog, carried));
    }

    [Fact]
    public void UnknownGeometryAndAMovingFloorCannotBecomeEmergencyConstruction()
    {
        var (map, catalog, observed, carried) = Fixture();
        Assert.Null(new PortableDefensePlanner().Find(observed, map with { Items = new Dictionary<string, PlaceableItem>() }, catalog, carried));
        var belt = map.Prototypes["wall"] with { Name = "belt", Type = "transport-belt" };
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["belt"] = belt },
            Entities = [new("moving-floor", "belt", new(0, 0), new(new(-.5, -.5), new(.5, .5)), 0, "agent")] };
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog, carried));
    }

    [Fact]
    public void SupplySelectionRequiresNativeCompatibleTurretAndCarriedStockOrEnabledHandCrafting()
    {
        var (_, catalog, _, _) = Fixture();
        var empty = new Dictionary<string, long>();
        Assert.Equal("native-turret", PortableDefensePlanner.SupplyTurret(catalog, empty));
        catalog = catalog with { Recipes = [] };
        Assert.Null(PortableDefensePlanner.SupplyTurret(catalog, empty));
        Assert.Equal("native-turret", PortableDefensePlanner.SupplyTurret(catalog, new Dictionary<string, long> { ["native-turret"] = 1 }));
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
            { ["native-turret"] = catalog.Items["native-turret"] with { PlaceEntityType = "electric-turret" } } };
        Assert.Null(PortableDefensePlanner.SupplyTurret(catalog, new Dictionary<string, long> { ["native-turret"] = 1 }));
    }

    [Theory]
    [InlineData("native-name")]
    [InlineData("entity-type")]
    [InlineData("unsupported-ammo")]
    public void ContradictoryNativeBindingOrANonBulletTurretCannotChooseAConstruction(string mismatch)
    {
        var (map, catalog, observed, carried) = Fixture();
        if (mismatch == "unsupported-ammo")
            catalog = catalog with { Turrets = new Dictionary<string, NativeTurret>
                { ["native-turret"] = catalog.Turrets!["native-turret"] with { AmmoCategories = ["cannon-shell"] } } };
        else
            catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
                { ["native-turret"] = catalog.Items["native-turret"] with
                    { PlaceEntity = mismatch == "native-name" ? "another-native-entity" : "turret-entity",
                      PlaceEntityType = mismatch == "entity-type" ? "electric-turret" : "ammo-turret" } } };
        carried["incompatible-rounds"] = 100;
        Assert.Null(new PortableDefensePlanner().Find(observed, map, catalog, carried));
    }

    [Fact]
    public void CancellationStopsPlanningBeforeAPlacementCanBeChosen()
    {
        var (map, catalog, observed, carried) = Fixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PortableDefensePlanner().Find(observed, map, catalog, carried, cancelled.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyNativeEnemyTypeMetadataEnablesPackConstructionWhileLegacyFramesStillPermitGunDefense(bool typed)
    {
        var (map, catalog, _, carried) = Fixture();
        var enemies = Enumerable.Range(0, 3).Select(i =>
        {
            var node = new Dictionary<string, object> { ["id"] = "visible-" + i, ["position"] = new MapPosition(12 + i, 0), ["collectedTick"] = 100L };
            if (typed) node["type"] = "unit";
            return node;
        }).ToArray();
        var response = new GameResponse(1, "native-frame", true, 100, Protocol.ToElement(new
        {
            scope = map.Scope, collectedTick = 100,
            coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemiesTruncated = false },
            agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = new MapPosition(0, 0),
                health = 250, maxHealth = 250, weapon = new { ready = true, rounds = 200, range = 18 } },
            enemies
        }));
        var observed = SafetyObservation.Parse(response);
        Assert.NotNull(DefensePolicy.SelectTarget(observed));
        Assert.All(observed.Enemies, enemy => Assert.Equal(typed ? "unit" : null, enemy.Type));
        var plan = new PortableDefensePlanner().Find(observed, map, catalog, carried);
        Assert.Equal(typed, plan is not null);
    }

    internal static (SpatialSnapshot Map, ProductionCatalog Catalog, SafetyObservation Observed, Dictionary<string, long> Carried) Fixture()
    {
        var map = SpatialPlannerTests.Map([]);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["turret-entity"] = map.Prototypes["furnace"] with { Name = "turret-entity", Type = "ammo-turret" } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["native-turret"] = new("turret-entity", 50) } };
        var catalog = Catalogs.Early() with { Scope = map.Scope, CollectedTick = map.CollectedTick,
            Turrets = new Dictionary<string, NativeTurret> { ["native-turret"] = new("turret-entity", 18, ["bullet"]) },
            Items = new Dictionary<string, NativeItem> { ["native-turret"] = new(0, 50, PlaceEntity: "turret-entity", PlaceEntityType: "ammo-turret"),
                ["basic-rounds"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 5),
                ["strong-rounds"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 8),
                ["incompatible-rounds"] = new(0, 200, AmmoCategory: "cannon-shell", MagazineSize: 1, RoundDamage: 100) },
            Recipes = [new("turret-recipe", true, "crafting", 1, [], [new("native-turret", "item", 1)], false)] };
        var observed = new SafetyObservation(100, map.Scope, true, "ai", false, new(0, 0), 250, new(true, 200, 18),
            [new("enemy-a", new(12, 0), "unit"), new("enemy-b", new(13, 2), "unit"), new("enemy-c", new(14, -1), "unit")], null,
            MaxHealth: 250, LocalEnemiesComplete: true);
        return (map, catalog, observed, new Dictionary<string, long> { ["native-turret"] = 2, ["basic-rounds"] = 60 });
    }
}
