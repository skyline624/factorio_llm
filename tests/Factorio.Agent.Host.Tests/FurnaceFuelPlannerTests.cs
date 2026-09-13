using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceFuelPlannerTests
{
    [Fact]
    public void SteelBatchUsesNativeEnergyAndAnObtainableResourceFuel()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        var result = FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 20, catalog, new Dictionary<string, long>(), new Dictionary<string, long>(), false);
        Assert.Equal("coal", result.Fuel);
        Assert.Equal(28_800_000, result.WorkJoules);
        Assert.Equal(9, result.Reserve);
    }

    [Fact]
    public void SufficientCarriedWoodIsUsedWithoutHarvestingMore()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        var stock = new Dictionary<string, long> { ["wood"] = 20 };
        Assert.Equal("wood", FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 20, catalog, stock, stock, false).Fuel);
    }

    [Fact]
    public void SmallExistingWoodStockDoesNotAuthorizeBulkTreeHarvesting()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        var stock = new Dictionary<string, long> { ["wood"] = 1 };
        var result = FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 20, catalog, stock, stock, false);
        Assert.Equal("wood", result.Fuel);
        Assert.Equal(1, result.Reserve);
    }

    [Fact]
    public void HarvestableTreesAreNotOrdinaryProductionFuel()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        catalog = catalog with { Mining = new Dictionary<string, NativeMaterial[]> { ["tree"] = [new("wood", "item", 4)] } };
        Assert.Throws<InvalidOperationException>(() => FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 20, catalog,
            new Dictionary<string, long>(), new Dictionary<string, long>(), false));
        Assert.Equal("wood", FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 20, catalog,
            new Dictionary<string, long>(), new Dictionary<string, long>(), true).Fuel);
    }

    [Fact]
    public void FuelReserveIsCappedByNativeStack()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        Assert.Equal(50, FurnaceFuelPlanner.Choose(recipe, furnace, geometry, 1000, catalog,
            new Dictionary<string, long>(), new Dictionary<string, long>(), false).Reserve);
    }

    [Fact]
    public void UnknownEnergyCannotBecomeAnArbitraryFuelTrip()
    {
        var (recipe, furnace, geometry, catalog) = Setup();
        Assert.Throws<InvalidDataException>(() => FurnaceFuelPlanner.Choose(recipe, furnace, geometry with { EnergyPerTick = null }, 20,
            catalog, new Dictionary<string, long>(), new Dictionary<string, long>(), false));
    }

    private static (NativeRecipe, NativeFurnace, EntityGeometry, ProductionCatalog) Setup()
    {
        var recipe = new NativeRecipe("steel", true, "smelting", 16, [new("plate", "item", 5)], [new("steel", "item", 1)], true);
        var furnace = new NativeFurnace("furnace", new Dictionary<string, bool> { ["smelting"] = true },
            new Dictionary<string, bool> { ["chemical"] = true }, 1);
        var geometry = new EntityGeometry("furnace", "furnace", new(new(-.7, -.7), new(.7, .7)), new([], false, false, false),
            2, 2, EnergyPerTick: 1500, BurnerEffectivity: 1);
        var catalog = new ProductionCatalog(new("w", "s", "a", 1, 1), 1, [recipe],
            new Dictionary<string, NativeItem> { ["coal"] = new(4_000_000, 50, "chemical"), ["wood"] = new(2_000_000, 100, "chemical") },
            new Dictionary<string, NativeMaterial[]> { ["coal"] = [new("coal", "item", 1)], ["tree"] = [new("wood", "item", 4)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>(),
            MiningSourceTypes: new Dictionary<string, string> { ["coal"] = "resource", ["tree"] = "tree" });
        return (recipe, furnace, geometry, catalog);
    }
}
