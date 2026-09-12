using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SmeltingPlannerTests
{
    [Fact]
    public void CarriedDrillCanSupplyAnExistingCompatibleFurnace()
    {
        var (map, catalog) = Setup();
        var plan = new SmeltingPlanner().Find("plate", catalog, map,
            new Dictionary<string, long> { ["drill-item"] = 1 }, Owned());
        Assert.NotNull(plan);
        Assert.Null(plan.ExistingDrillId);
        Assert.Equal("receiver", plan.Connection.ReceiverId);
        Assert.Equal("smelt-plate", plan.Recipe.Name);
    }

    [Fact]
    public void InstalledDrillIsReusedWithoutAnotherDrillInInventory()
    {
        var (map, catalog) = Setup(installed: true);
        var plan = new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(), Owned());
        Assert.NotNull(plan);
        Assert.Equal("installed", plan.ExistingDrillId);
        Assert.Equal(new MapPosition(0, 2), plan.Connection.Drill.Position);
        Assert.Equal(0, plan.Connection.Drill.Direction);
    }

    [Fact]
    public void InconsistentNativeDropPositionCannotBeReused()
    {
        var (map, catalog) = Setup(installed: true);
        map = map with
        {
            Entities = map.Entities.Select(e => e.Id == "installed"
            ? e with { DropPosition = new(0.4, 0.4) } : e).ToArray()
        };
        Assert.Null(new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(), Owned()));
    }

    [Theory]
    [InlineData("other-recipe", 100)]
    [InlineData(null, 0)]
    public void OccupiedFurnaceOrExhaustedPatchIsNotAnOpportunity(string? recipe, long amount)
    {
        var (map, catalog) = Setup(installed: true);
        map = map with { Entities = map.Entities.Select(e => e.Id == "deposit" ? e with { Amount = amount } : e).ToArray() };
        var owned = Owned();
        owned["receiver"] = owned["receiver"] with { Recipe = recipe };
        Assert.Null(new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(), owned));
    }

    [Fact]
    public void UnownedReceiverIsNotSelected()
    {
        var (map, catalog) = Setup(installed: true);
        Assert.Null(new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine> { ["installed"] = new("installed", "drill", null) }));
    }

    [Fact]
    public void HandCraftableTargetIsNotRoutedToExtraction()
    {
        var (map, catalog) = Setup(installed: true);
        catalog = catalog with { HandCategories = new Dictionary<string, bool> { ["smelting"] = true } };
        Assert.Null(new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(), Owned()));
    }

    [Fact]
    public void ReceiverWithNoRecipeButForeignOutputCannotAcceptAutomatedSmelting()
    {
        var (map, catalog) = Setup(installed: true);
        var owned = Owned();
        owned["receiver"] = owned["receiver"] with { Output = new Dictionary<string, long> { ["other-plate"] = 6 } };
        Assert.Null(new SmeltingPlanner().Find("plate", catalog, map, new Dictionary<string, long>(), owned));
    }

    [Fact]
    public void ObservationsAcrossPilotTransitionCannotBeCombined()
    {
        var (map, catalog) = Setup(installed: true);
        catalog = catalog with { Scope = catalog.Scope with { Generation = 2 } };
        Assert.Throws<InvalidDataException>(() => new SmeltingPlanner().Find("plate", catalog, map,
            new Dictionary<string, long>(), Owned()));
    }

    private static Dictionary<string, KnownProductionMachine> Owned() => new()
        { ["receiver"] = new("receiver", "furnace", null), ["installed"] = new("installed", "drill", null) };

    internal static (SpatialSnapshot Map, ProductionCatalog Catalog) Setup(bool installed = false)
    {
        var (map, catalog) = ExtractionPlannerTests.Setup(0, 0);
        var fuel = new Dictionary<string, bool> { ["chemical"] = true };
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { FuelCategories = fuel } }
        };
        if (installed) map = map with
        {
            Entities = [.. map.Entities,
            new("installed", "drill", new(0, 2), new(new(-0.7, 1.3), new(0.7, 2.7)), 0, "agent",
                DropPosition: new(0, 0.15234375), DropTargetId: "receiver")]
        };
        catalog = catalog with
        {
            Recipes = [new("smelt-plate", true, "smelting", 3.2, [new("ore", "item", 1)], [new("plate", "item", 1)], false)],
            Items = new Dictionary<string, NativeItem> { ["drill-item"] = new(0, 50, PlaceEntity: "drill", PlaceEntityType: "mining-drill") },
            Machines = new Dictionary<string, NativeFurnace>
            { ["furnace-item"] = new("furnace", new Dictionary<string, bool> { ["smelting"] = true }, fuel, 1) }
        };
        return (map, catalog);
    }
}
