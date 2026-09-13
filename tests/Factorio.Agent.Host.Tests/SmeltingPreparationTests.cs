using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SmeltingPreparationTests
{
    [Fact]
    public void BootstrapMaterialsDoNotRecursivelyCommissionAnotherExtractionSite()
    {
        var (map, catalog) = Setup();
        var step = new ProductionPlanner().Next("plate", 3, new Dictionary<string, long>(), catalog, map,
            [new("known", "furnace", null)], allowExtractionPreparation: false);
        Assert.Equal("mine", step.Kind);
        Assert.Equal("ore", step.Item);
        Assert.Equal(3, step.Quantity);
    }

    [Fact]
    public void AvailableFurnaceIsUsedBeforeManufacturingAnotherModel()
    {
        var (map, catalog) = Setup();
        map = map with { Items = new Dictionary<string, PlaceableItem>(map.Items)
            { ["advanced-furnace"] = map.Items["furnace-item"] } };
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
            { ["advanced-furnace"] = catalog.Items["furnace-item"] },
            Machines = new Dictionary<string, NativeFurnace>(catalog.Machines)
            { ["advanced-furnace"] = catalog.Machines["furnace-item"] },
            Recipes = [.. catalog.Recipes, new("advanced-furnace", true, "crafting", 3,
                [new("plate", "item", 30)], [new("advanced-furnace", "item", 1)], false)] };
        var site = new SmeltingPreparationPlanner().Find(map, catalog,
            new Dictionary<string, long> { ["drill-item"] = 1, ["furnace-item"] = 1 }, "plate");
        Assert.NotNull(site);
        Assert.Equal("furnace-item", site.Equipment.FurnaceItem);
    }

    [Fact]
    public void NewDirectSmeltingSiteSupportsAnElectricDrill()
    {
        var (map, catalog) = Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { IsElectric = true, FuelCategories = null } } };
        var site = new SmeltingPreparationPlanner().Find(map, catalog, new Dictionary<string, long>(), "plate");
        Assert.NotNull(site);
        Assert.Equal("drill-item", site.Equipment.DrillItem);
    }

    [Fact]
    public void LockedDrillRecipesDoNotPromiseConstructibleMachines()
    {
        var (_, catalog) = Setup();
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "drill" ? r with { Enabled = false } : r).ToArray() };
        Assert.Empty(new SmeltingPreparationPlanner().Options(catalog, new Dictionary<string, long>(), "plate"));
    }

    [Fact]
    public void MixedOreDoesNotBecomeAWorkingSmeltingSite()
    {
        var (map, catalog) = Setup();
        map = map with
        {
            Entities = [.. map.Entities, map.Entities.Single(e => e.Id == "deposit") with { Id = "foreign", Name = "other-resource" }],
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["other-resource"] = map.Prototypes["resource"] with { Name = "other-resource" } }
        };
        Assert.Null(new SmeltingPreparationPlanner().Find(map, catalog, new Dictionary<string, long>(), "plate"));
    }

    [Fact]
    public void BulkPlatesPrepareMachinesBeforeRequestingHandMinedOre()
    {
        var (map, catalog) = Setup();
        var step = new ProductionPlanner().Next("plate", 75, new Dictionary<string, long>(), catalog, map, []);
        Assert.Equal("prepare-smelting", step.Kind);
        Assert.Equal("plate", step.Item);
        Assert.Equal(75, step.Quantity);
    }

    [Fact]
    public void ANewSitePlacesBothMachinesFromResourceAndNativeGeometry()
    {
        var (map, catalog) = Setup();
        var site = new SmeltingPreparationPlanner().Find(map, catalog, new Dictionary<string, long>(), "plate");
        Assert.NotNull(site);
        Assert.True(ExtractionPlanner.DropTile(site.Connection.OutputPosition).Overlaps(
            map.Prototypes["furnace"].CollisionBox.Rotate(site.Furnace.Direction).Translate(site.Furnace.Position)));
        Assert.Contains("deposit", site.Connection.ResourceIds);
        Assert.True(new SpatialCollisionField(map).PlacementClear(map.Prototypes["furnace"], site.Furnace.Position, site.Furnace.Direction));
    }

    internal static (SpatialSnapshot Map, ProductionCatalog Catalog) Setup()
    {
        var (map, catalog) = SmeltingPlannerTests.Setup();
        map = map with { Entities = map.Entities.Where(e => e.Id != "receiver").ToArray(),
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["furnace-item"] = new("furnace", 50) } };
        catalog = catalog with
        {
            HandCategories = new Dictionary<string, bool> { ["crafting"] = true },
            Items = new Dictionary<string, NativeItem>(catalog.Items)
                { ["ore"] = new(0, 100), ["furnace-item"] = new(0, 50, PlaceEntity: "furnace", PlaceEntityType: "furnace") },
            Recipes = [.. catalog.Recipes,
                new("drill", true, "crafting", 2, [new("plate", "item", 3)], [new("drill-item", "item", 1)], false),
                new("furnace", true, "crafting", 2, [new("stone", "item", 5)], [new("furnace-item", "item", 1)], false)]
        };
        return (map, catalog);
    }
}
