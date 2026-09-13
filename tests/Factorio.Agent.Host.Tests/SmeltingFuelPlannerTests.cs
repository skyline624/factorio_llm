using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SmeltingFuelPlannerTests
{
    [Fact]
    public void PartialWoodStockDoesNotAuthorizeHarvestingTheRestOfTheReserve()
    {
        var (map, catalog, plan) = Setup();
        var wood = new Dictionary<string, long> { ["wood"] = 1 };
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, wood, wood);
        Assert.Equal("coal", fuel.Fuel);
    }

    [Fact]
    public void TreesAloneAreNotAnOrdinaryFuelSupply()
    {
        var (map, catalog, plan) = Setup();
        catalog = catalog with { Mining = catalog.Mining.Where(p => p.Key != "coal-ore")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
        Assert.Throws<InvalidOperationException>(() => new SmeltingFuelPlanner().Choose(plan, map, catalog, 25,
            new Dictionary<string, long>(), new Dictionary<string, long>()));
        Assert.True(new SmeltingFuelPlanner().Choose(plan, map, catalog, 25,
            new Dictionary<string, long>(), new Dictionary<string, long>(), allowManualBootstrap: true).CanProduce);
    }

    [Fact]
    public void LoadedOreAllowsStoredWoodToSupplyOnlyTheFurnace()
    {
        var (map, catalog, plan) = Setup();
        var wood = new Dictionary<string, long> { ["wood"] = 5 };
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, wood, wood, includeDrillReserve: false);
        Assert.Equal("wood", fuel.Fuel);
        Assert.False(fuel.CanProduce);
        Assert.Equal(0, fuel.DrillReserve);
        Assert.Equal(5, fuel.FurnaceReserve);
    }

    [Fact]
    public void DepletedWoodIsReplacedWithMechanicalFuelOnReassessment()
    {
        var (map, catalog, plan) = Setup();
        var planner = new SmeltingFuelPlanner();
        var wood = new Dictionary<string, long> { ["wood"] = 30 };
        Assert.False(planner.Choose(plan, map, catalog, 25, wood, wood).CanProduce);
        var next = planner.Choose(plan, map, catalog, 15, new Dictionary<string, long>(), new Dictionary<string, long>());
        Assert.Equal("coal", next.Fuel);
        Assert.True(next.CanProduce);
    }

    [Fact]
    public void StoredManufacturedFuelDoesNotNeedAnInventedMiningSource()
    {
        var (map, catalog, plan) = Setup();
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
            { ["solid-fuel"] = new(12000000, 50, "chemical") } };
        var stored = new Dictionary<string, long> { ["solid-fuel"] = 20 };
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, stored, stored);
        Assert.Equal("solid-fuel", fuel.Fuel);
    }

    [Fact]
    public void SufficientLoadedOreExcludesTheDrillFromFuelProcurement()
    {
        var plan = new SmeltingFuelPlan("wood", 10, 5, 15000000, 7200000, 1.25);
        Assert.Equal(5, plan.ProcurementTarget(false, 1, 0, 0, includeDrillReserve: false));
        Assert.Equal(15, plan.ProcurementTarget(false, 1, 0, 0));
    }

    [Theory]
    [InlineData(true, 1, 0, 0, 15)]
    [InlineData(true, 15, 0, 0, 0)]
    [InlineData(false, 5, 9, 0, 0)]
    [InlineData(true, 1, 0, 3, 12)]
    public void ProcurementDoesNotLeaveAnEmptyFurnaceWhenItsFuelIsAlreadyCarried(
        bool refuellingDrill, long carried, long drillStock, long furnaceStock, int expected)
    {
        var plan = new SmeltingFuelPlan("wood", 10, 5, 15000000, 7200000, 1.25);
        Assert.Equal(expected, plan.ProcurementTarget(refuellingDrill, carried, drillStock, furnaceStock));
    }

    [Fact]
    public void PreparesFuelForBothMachinesFromNativeRatesAndAvailableStock()
    {
        var (map, catalog, plan) = Setup();
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["wood"] = 30 });
        Assert.Equal("wood", fuel.Fuel);
        Assert.Equal(15000000, fuel.DrillWorkJoules);
        Assert.Equal(7200000, fuel.FurnaceWorkJoules);
        Assert.Equal(10, fuel.DrillReserve);
        Assert.Equal(5, fuel.FurnaceReserve);
    }

    [Fact]
    public void ElectricDrillWorkDoesNotCreateABurnerFuelReserve()
    {
        var (map, catalog, plan) = Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { IsElectric = true, FuelCategories = null,
                MiningSpeed = .5, EnergyPerTick = 1500, BurnerEffectivity = null } } };
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["wood"] = 30 });
        Assert.Equal(4500000, fuel.DrillWorkJoules);
        Assert.Equal(0, fuel.DrillReserve);
        Assert.Equal(5, fuel.FurnaceReserve);
        Assert.Equal(5, fuel.ProcurementTarget(false, 0, 0, 0));
    }

    [Fact]
    public void MissingMiningRateDoesNotBecomeAnArbitraryTwoItemTrip()
    {
        var (map, catalog, plan) = Setup();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { MiningSpeed = null } } };
        Assert.Throws<InvalidDataException>(() => new SmeltingFuelPlanner().Choose(plan, map, catalog, 25,
            new Dictionary<string, long>(), new Dictionary<string, long>()));
    }

    [Fact]
    public void ReservesStayWithinNativeFuelStacks()
    {
        var (map, catalog, plan) = Setup();
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items) { ["wood"] = new(2000000, 3, "chemical") } };
        var fuel = new SmeltingFuelPlanner().Choose(plan, map, catalog, 25, new Dictionary<string, long>(), new Dictionary<string, long> { ["wood"] = 6 });
        Assert.Equal(3, fuel.DrillReserve);
        Assert.Equal(3, fuel.FurnaceReserve);
    }

    private static (SpatialSnapshot, ProductionCatalog, SmeltingPlan) Setup()
    {
        var (map, catalog) = SmeltingPlannerTests.Setup(installed: true);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["drill"] = map.Prototypes["drill"] with { MiningSpeed = .25, EnergyPerTick = 2500, BurnerEffectivity = 1 },
            ["furnace"] = map.Prototypes["furnace"] with { EnergyPerTick = 1500, BurnerEffectivity = 1 },
            ["resource"] = map.Prototypes["resource"] with { MiningTime = 1 }
        } };
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
                { ["wood"] = new(2000000, 100, "chemical"), ["coal"] = new(4000000, 50, "chemical") },
            Mining = new Dictionary<string, NativeMaterial[]>(catalog.Mining)
                { ["tree"] = [new("wood", "item", 4)], ["coal-ore"] = [new("coal", "item", 1)] },
            MiningSourceTypes = new Dictionary<string, string> { ["tree"] = "tree", ["coal-ore"] = "resource" } };
        var plan = new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine> { ["receiver"] = new("receiver", "furnace", null), ["installed"] = new("installed", "drill", null) })!;
        return (map, catalog, plan);
    }
}
