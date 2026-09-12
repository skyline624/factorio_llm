using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FuelReservePlannerTests
{
    [Theory]
    [InlineData(44, true, 6)]
    [InlineData(44, false, 0)]
    [InlineData(40, false, 0)]
    [InlineData(39, false, 11)]
    public void KeepsTheExistingFuelAndReplenishesBeforeExhaustion(int amount, bool reserve, int missing)
    {
        var (map, stock, catalog) = Setup("wood", amount);
        var plan = new FuelReservePlanner().Choose(map, stock, catalog, "boiler", 1,
            new Dictionary<string, long> { ["coal"] = 100 }, 10000, reserve);
        Assert.NotNull(plan);
        Assert.Equal("wood", plan.Fuel);
        Assert.Equal("chest", plan.ChestId);
        Assert.Equal(50, plan.TargetStock);
        Assert.Equal(missing, plan.RefillAmount);
    }

    [Fact]
    public void RefusesAChestWithNonFuelContentsInsteadOfMixingSupplies()
    {
        var (map, stock, catalog) = Setup("iron-plate", 1);
        Assert.Throws<InvalidDataException>(() => new FuelReservePlanner().Choose(map, stock, catalog, "boiler", 1,
            new Dictionary<string, long>(), 1000, true));
    }

    [Fact]
    public void IgnoresAnArmThatNoLongerFeedsThisBoiler()
    {
        var (map, stock, catalog) = Setup("wood", 1);
        map = map with { Entities = map.Entities.Select(e => e.Id == "arm" ? e with { DropTargetId = "elsewhere" } : e).ToArray() };
        Assert.Null(new FuelReservePlanner().Choose(map, stock, catalog, "boiler", 1, new Dictionary<string, long>(), 1000, true));
    }

    [Theory]
    [InlineData("chest")]
    [InlineData("arm")]
    public void MissingNativeStockMustNotBecomeAnEmptyReserve(string missingEntity)
    {
        var (map, stock, catalog) = Setup("wood", 1);
        stock = stock with { Records = stock.Records.Where(r => r.EntityId != missingEntity).ToArray() };
        Assert.Throws<InvalidDataException>(() => new FuelReservePlanner().Choose(map, stock, catalog, "boiler", 1,
            new Dictionary<string, long>(), 1000, true));
    }

    [Fact]
    public void ASecondConsumerPreventsClaimingAnExclusiveBoilerReserve()
    {
        var (map, stock, catalog) = Setup("wood", 1);
        map = map with { Entities = [.. map.Entities, map.Entities.Single(e => e.Id == "arm") with
            { Id = "other-arm", DropTargetId = "elsewhere" }] };
        Assert.Throws<InvalidDataException>(() => new FuelReservePlanner().Choose(map, stock, catalog, "boiler", 1,
            new Dictionary<string, long>(), 1000, true));
    }

    private static (SpatialSnapshot Map, FactorySnapshot Stock, ProductionCatalog Catalog) Setup(string fuel, int amount)
    {
        var map = FuelFeederPlannerTests.Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["boiler"] = map.Prototypes["boiler"] with { FuelCategories = new Dictionary<string, bool> { ["chemical"] = true } } },
            Entities = [.. map.Entities,
                new("chest", "chest", new(2.5, .5), new(new(2.15, .15), new(2.85, .85)), 0, "agent"),
                new("arm", "inserter", new(3.5, .5), new(new(3.35, .35), new(3.65, .65)), 12, "agent",
                    DropTargetId: "boiler", Power: new(100, 1), PickupTargetId: "chest")] };
        var stock = new FactorySnapshot("s", map.Scope, 1, 100, Protocol.ToElement(new { }),
            [new("chest-inventory", "inventory", "chest", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { [fuel] = amount } })),
             new("arm-hand", "transit", "arm", "inserter-hand", Protocol.ToElement(new { items = new Dictionary<string, long>() }))]);
        var catalog = new ProductionCatalog(map.Scope, 1, [],
            new Dictionary<string, NativeItem> { ["wood"] = new(2000000, 100, "chemical"), ["coal"] = new(4000000, 50, "chemical") },
            new Dictionary<string, NativeMaterial[]> { ["tree"] = [new("wood", "item", 4)], ["coal"] = [new("coal", "item", 1)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        return (map, stock, catalog);
    }
}
