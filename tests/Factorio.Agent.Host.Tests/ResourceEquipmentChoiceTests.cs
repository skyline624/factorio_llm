using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceEquipmentChoiceTests
{
    [Fact]
    public void ThePreferredFurnaceRemainsFirstAndAlternativesRetainTheDrillAndPower()
    {
        var (map, catalog) = Setup();
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog), None);
        Assert.Equal(["steel-furnace", "stone-furnace"], choices.Select(e => e.Furnace));
        Assert.Equal(ResourceCellPlanner.Equipment(catalog, map, Supply(catalog), None), choices[0]);
        Assert.All(choices, e => Assert.Equal(choices[0] with { Furnace = e.Furnace }, e));
    }

    [Fact]
    public void AnUnresearchedUncarriedFurnaceIsExcluded()
    {
        var (map, catalog) = Setup(enabled: false);
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog), None);
        Assert.Equal("stone-furnace", Assert.Single(choices).Furnace);
    }

    [Fact]
    public void ACarriedFurnaceIsUsableEvenWhenItsRecipeIsLocked()
    {
        var (map, catalog) = Setup(enabled: false);
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog),
            new Dictionary<string, long> { ["steel-furnace"] = 1 });
        Assert.Equal(["steel-furnace", "stone-furnace"], choices.Select(e => e.Furnace));
    }

    [Fact]
    public void AnIncompatibleFurnaceCannotBecomeAnAlternative()
    {
        var (map, catalog) = Setup();
        catalog = catalog with { Machines = new Dictionary<string, NativeFurnace>(catalog.Machines)
        {
            ["steel-furnace"] = catalog.Machines["steel-furnace"] with { Categories = new Dictionary<string, bool> { ["crafting"] = true } }
        } };
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog), None);
        Assert.Equal("stone-furnace", Assert.Single(choices).Furnace);
    }

    [Fact]
    public void MissingNativeGeometryCannotProduceAnAlternative()
    {
        var (map, catalog) = Setup();
        map = map with { Prototypes = map.Prototypes.Where(p => p.Key != "steel-furnace").ToDictionary() };
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog), None);
        Assert.Equal("stone-furnace", Assert.Single(choices).Furnace);
    }

    [Fact]
    public void MinersDoNotAddUnrelatedFurnacesAndMissingSmelterPowerRemainsUnsupported()
    {
        var (map, catalog) = Setup();
        Assert.Null(Assert.Single(ResourceCellPlanner.EquipmentCandidates(catalog, map,
            ResourceCellPlanner.Supply(catalog, "coal")!, None)).Furnace);
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name is "inserter" or "small-electric-pole"
            ? r with { Enabled = false } : r).ToArray() };
        Assert.Empty(ResourceCellPlanner.EquipmentCandidates(catalog, map, Supply(catalog), None));
    }

    [Fact]
    public void ASlowerFurnaceMakesAReserveViableAtItsOwnRateWithoutRelaxingTenMinutes()
    {
        var (map, catalog) = Setup();
        var supply = Supply(catalog);
        var choices = ResourceCellPlanner.EquipmentCandidates(catalog, map, supply, None);
        var planner = new ResourceCellPlanner();
        Assert.Equal(ResourceRowSearchStatus.NoSite, planner.Find(map, catalog, supply, choices[0], 1, new(0, 0)).Status);
        var slow = planner.Find(map, catalog, supply, choices[1], 1, new(0, 0));
        Assert.Equal(ResourceRowSearchStatus.Found, slow.Status);
        Assert.Equal(18.75, slow.Row!.CellPerMinute);
        Assert.True(planner.Fits(map, catalog, slow.Row, 0));
        var exhausted = map with { Entities = map.Entities.Select(e => e with { Amount = 180 }).ToArray() };
        Assert.Equal(ResourceRowSearchStatus.NoSite, planner.Find(exhausted, catalog, supply, choices[1], 1, new(0, 0)).Status);
        Assert.False(planner.Fits(exhausted, catalog, slow.Row, 0));
    }

    private static readonly IReadOnlyDictionary<string, long> None = new Dictionary<string, long>();
    private static ResourceSupply Supply(ProductionCatalog catalog) => ResourceCellPlanner.Supply(catalog, "iron-plate")!;

    // Synthetic availability catalog; both furnace shapes match the base-game geometry and only their speed differs.
    private static (SpatialSnapshot Map, ProductionCatalog Catalog) Setup(bool enabled = true)
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("iron-ore", 0, 0, 1, 1, amount: 200).ToArray());
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["steel-furnace"] = map.Prototypes["stone-furnace"] with { Name = "steel-furnace" } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["steel-furnace"] = new("steel-furnace", 50) }
        };
        var catalog = Catalogs.Raw();
        catalog = catalog with
        {
            Items = new Dictionary<string, NativeItem>(catalog.Items)
                { ["steel-furnace"] = catalog.Items["stone-furnace"] with { PlaceEntity = "steel-furnace" } },
            Recipes = [.. catalog.Recipes, new("steel-furnace", enabled, "crafting", 3,
                [new("steel-plate", "item", 6), new("stone-brick", "item", 10)], [new("steel-furnace", "item", 1)], false)],
            Machines = new Dictionary<string, NativeFurnace>(catalog.Machines)
                { ["steel-furnace"] = catalog.Machines["stone-furnace"] with { EntityName = "steel-furnace", CraftingSpeed = 2 } }
        };
        return (map, catalog);
    }
}
