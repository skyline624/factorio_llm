using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceExtractionTests
{
    [Fact]
    public void TreeHarvestKeepsItsShortApproachDespiteALargerGeneralReach()
    {
        var (map, _) = Setup();
        var source = map.Entities.First(e => map.Prototypes[e.Name].Type == "resource");
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { [source.Name] = map.Prototypes[source.Name] with { Type = "tree" } },
            Actor = map.Actor with { ReachDistance = 10, ResourceReachDistance = 2.7 } };
        Assert.InRange(ProductionController.MiningDistance(source, map), .2, 3);
    }

    [Fact]
    public void StarterResourceUnderDrillCanBeMinedFromTheNativeResourceReach()
    {
        var (map, _) = Setup();
        var resource = map.Entities.First(e => map.Prototypes[e.Name].Type == "resource");
        var position = resource.Position;
        var drill = map.Prototypes["drill"];
        map = map with { Actor = map.Actor with { Position = new(position.X - 2, position.Y), ResourceReachDistance = 2.7 },
            Entities = [..map.Entities, new("installed", "drill", position,
                drill.CollisionBox.Translate(position), 0, "agent")] };
        var field = new SpatialCollisionField(map);
        Assert.False(field.Walkable(position));
        double distance = ProductionController.MiningDistance(resource, map);
        Assert.InRange(distance, 2, 2.5);
        Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(field, position, distance - .2).Status);
    }

    [Fact]
    public void BulkRawOreRequestsMachinesBeforeManualMining()
    {
        var (map, catalog) = Setup();
        var step = new ProductionPlanner().Next("ore", 100, new Dictionary<string, long>(), catalog, map, []);
        Assert.Equal("extract", step.Kind);
        Assert.Equal(100, step.Quantity);
    }

    [Fact]
    public void MachineBootstrapStillPermitsOnlyItsRequiredManualMaterials()
    {
        var (map, catalog) = Setup();
        var step = new ProductionPlanner().Next("ore", 3, new Dictionary<string, long>(), catalog, map, [], allowExtractionPreparation: false);
        Assert.Equal("mine", step.Kind);
        Assert.Equal(3, step.Quantity);
    }

    [Fact]
    public void TreesAreNotDrillResources()
    {
        var (_, catalog) = Setup();
        Assert.Empty(new StoredResourceExtractionPlanner().Options(catalog, new Dictionary<string, long>(), "wood"));
    }

    [Fact]
    public void NewChestAndDrillShareANativeDropTile()
    {
        var (map, catalog) = Setup();
        var plan = new StoredResourceExtractionPlanner().Find("ore", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine>());
        Assert.NotNull(plan?.NewChest);
        Assert.True(ExtractionPlanner.DropTile(plan.Connection.OutputPosition).Overlaps(
            map.Prototypes["chest"].CollisionBox.Rotate(plan.NewChest.Direction).Translate(plan.NewChest.Position)));
        Assert.Null(plan.ExistingDrillId);
    }

    [Fact]
    public void ElectricExtractionEnergyUsesNativeTimeAndPowerWithoutABurnerEfficiency()
    {
        var (map, catalog) = Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { IsElectric = true, FuelCategories = null,
                MiningSpeed = .5, EnergyPerTick = 1500, BurnerEffectivity = null },
                ["resource"] = map.Prototypes["resource"] with { MiningTime = 1 } } };
        var plan = new StoredResourceExtractionPlanner().Find("ore", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine>())!;
        Assert.Equal(9000000, ExtractionPlanner.WorkEnergy(plan.Connection, map, "drill-item", catalog, "ore", 50));
    }

    [Fact]
    public void NewSolidExtractionCanChooseAnUnlockedElectricDrill()
    {
        var (map, catalog) = Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { IsElectric = true, FuelCategories = null } } };
        var plan = new StoredResourceExtractionPlanner().Find("ore", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine>());
        Assert.NotNull(plan);
        Assert.Equal("drill-item", plan.Equipment.DrillItem);
    }

    [Fact]
    public void InstalledConnectionIsReusedWithoutConstructionOrCarriedEquipment()
    {
        var (map, catalog) = Setup();
        var planner = new StoredResourceExtractionPlanner();
        var plan = planner.Find("ore", catalog, map, new Dictionary<string, long>(), new Dictionary<string, KnownProductionMachine>())!;
        var chestGeometry = map.Prototypes["chest"];
        var drillGeometry = map.Prototypes["drill"];
        map = map with { Entities = [..map.Entities,
            new("store", "chest", plan.NewChest!.Position, chestGeometry.CollisionBox.Rotate(plan.NewChest.Direction).Translate(plan.NewChest.Position), plan.NewChest.Direction, "agent"),
            new("rig", "drill", plan.Connection.Drill.Position, drillGeometry.CollisionBox.Rotate(plan.Connection.Drill.Direction).Translate(plan.Connection.Drill.Position),
                plan.Connection.Drill.Direction, "agent", DropPosition: plan.Connection.OutputPosition, DropTargetId: "store")] };
        var owned = new Dictionary<string, KnownProductionMachine> { ["store"] = new("store", "chest", null, Output: new Dictionary<string, long>()), ["rig"] = new("rig", "drill", null) };
        var reused = planner.Find("ore", catalog, map, new Dictionary<string, long>(), owned, allowConstruction: false);
        Assert.NotNull(reused);
        Assert.Equal("rig", reused.ExistingDrillId);
        Assert.Equal("store", reused.Connection.ReceiverId);
        Assert.Null(reused.NewChest);
        owned.Remove("store");
        Assert.Null(planner.Find("ore", catalog, map, new Dictionary<string, long>(), owned, allowConstruction: false));
    }

    internal static (SpatialSnapshot Map, ProductionCatalog Catalog) Setup()
    {
        var (map, catalog) = SmeltingPreparationTests.Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["chest"] = map.Prototypes["furnace"] with { Name = "chest", Type = "container" } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["chest-item"] = new("chest", 50) } };
        catalog = catalog with {
            MiningSourceTypes = new Dictionary<string, string> { ["resource"] = "resource", ["tree"] = "tree" },
            Mining = new Dictionary<string, NativeMaterial[]>(catalog.Mining) { ["tree"] = [new("wood", "item", 4)] },
            Items = new Dictionary<string, NativeItem>(catalog.Items)
                { ["chest-item"] = new(0, 50, PlaceEntity: "chest", PlaceEntityType: "container") },
            Recipes = [.. catalog.Recipes, new("chest", true, "crafting", .5, [new("wood", "item", 2)], [new("chest-item", "item", 1)], false)] };
        return (map, catalog);
    }
}
