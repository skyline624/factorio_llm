using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ReservedExtractionTests
{
    [Fact]
    public void ReadyRegisteredMinerIsReusedWhileEveryEntityStaysProtected()
    {
        var (map, catalog, state, cell) = Setup();
        using var scope = ProductionReservations.EnterFactory(new(1, "world", [], [cell]));
        var found = StoredResourceExtractionController.FindReservedConnection("ore", catalog, map, state);
        Assert.Equal("rig", found?.ExistingDrillId);
        Assert.Equal("store", found?.Connection.ReceiverId);
        Assert.Null(found?.NewChest);
        Assert.Contains("rig", ProductionReservations.Current);
        Assert.Contains("store", ProductionReservations.Current);
        Assert.True(ProductionReservations.Collects("store"));
        Assert.False(ProductionReservations.Collects("rig"));
    }

    [Theory]
    [InlineData("building")]
    [InlineData("smelter")]
    [InlineData("wrong-product")]
    [InlineData("missing-role")]
    [InlineData("missing-native-drill")]
    [InlineData("wrong-drop")]
    [InlineData("foreign-stock")]
    public void UnusableRegisteredPairCannotProposeConstructionOrBorrowAnotherReceiver(string fault)
    {
        var (map, catalog, state, cell) = Setup();
        if (fault == "building") cell = cell with { Status = "building" };
        if (fault == "smelter") cell = cell with { Kind = "smelter" };
        if (fault == "wrong-product") cell = cell with { Recipe = "coal" };
        if (fault == "missing-role") cell = cell with { Entities = new Dictionary<string, string> { ["drill"] = "rig" } };
        if (fault == "missing-native-drill") map = map with { Entities = map.Entities.Where(e => e.Id != "rig").ToArray() };
        if (fault == "wrong-drop") map = map with { Entities = map.Entities.Select(e => e.Id == "rig"
            ? e with { DropPosition = new(100, 100), DropTargetId = "other" } : e).ToArray() };
        if (fault == "foreign-stock") state = state with { Entities = state.Entities.Select(e => e.Id == "store"
            ? e with { Inventories = Protocol.ToElement(new { output = new { items = new Dictionary<string, long> { ["coal"] = 1 } } }) } : e).ToArray() };
        using var scope = ProductionReservations.EnterFactory(new(1, "world", [], [cell]));
        Assert.Null(StoredResourceExtractionController.FindReservedConnection("ore", catalog, map, state));
    }

    [Fact]
    public void RefuellingConfirmationIsRestrictedToTheSelectedRegisteredPair()
    {
        var (map, catalog, state, cell) = Setup();
        using var scope = ProductionReservations.EnterFactory(new(1, "world", [], [cell]));
        Assert.NotNull(StoredResourceExtractionController.FindReservedConnection("ore", catalog, map, state,
            drillId: "rig", chestId: "store"));
        Assert.Null(StoredResourceExtractionController.FindReservedConnection("ore", catalog, map, state,
            drillId: "rig", chestId: "different-chest"));
    }

    private static (SpatialSnapshot Map, ProductionCatalog Catalog, ProductionState State, FactoryCell Cell) Setup()
    {
        var (map, catalog) = ResourceExtractionTests.Setup();
        var plan = new StoredResourceExtractionPlanner().Find("ore", catalog, map, new Dictionary<string, long>(),
            new Dictionary<string, KnownProductionMachine>())!;
        var chest = plan.NewChest!;
        map = map with { Entities = [..map.Entities,
            new("store", "chest", chest.Position, map.Prototypes["chest"].CollisionBox.Rotate(chest.Direction).Translate(chest.Position), chest.Direction, "agent"),
            new("rig", "drill", plan.Connection.Drill.Position,
                map.Prototypes["drill"].CollisionBox.Rotate(plan.Connection.Drill.Direction).Translate(plan.Connection.Drill.Position),
                plan.Connection.Drill.Direction, "agent", DropPosition: plan.Connection.OutputPosition, DropTargetId: "store")] };
        var state = new ProductionState(catalog.Scope, 1, "ai", new Dictionary<string, long>(), [
            new("rig", "drill", plan.Connection.Drill.Position, null, Protocol.ToElement(new { })),
            new("store", "chest", chest.Position, null, Protocol.ToElement(new { output = new { items = new Dictionary<string, long>() } }))]);
        var cell = new FactoryCell("ore", 0, new(0, 0, true), "miner", "drill-item", "ore",
            new Dictionary<string, string> { ["drill"] = "rig", ["output-chest"] = "store" }, "ready", 1);
        return (map, catalog, state, cell);
    }
}
