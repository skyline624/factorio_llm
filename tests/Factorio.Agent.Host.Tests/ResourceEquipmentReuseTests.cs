using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceEquipmentReuseTests
{
    private static readonly IReadOnlyDictionary<string, int> Needed = new Dictionary<string, int>
        { ["steel-furnace"] = 1, ["electric-mining-drill"] = 1, ["iron-chest"] = 1, ["small-electric-pole"] = 1, ["inserter"] = 1 };

    [Fact]
    public void ExhaustedCellOffersItsIdleFurnaceAndDrillWhileKeepingItsStockAndPowerParts()
    {
        var (state, snapshot, catalog) = Fixture();
        var parts = ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed);
        Assert.Equal(["furnace", "drill"], parts.Select(p => p.Role));
        Assert.Equal(["steel-furnace", "electric-mining-drill"], parts.Select(p => p.Item));
        Assert.Equal(48, snapshot.Records.Single(r => r.Id == "fuel").Data.GetProperty("items").GetProperty("coal").GetInt64());
        Assert.Equal(100, snapshot.Records.Single(r => r.Id == "chest-stock").Data.GetProperty("items").GetProperty("iron-plate").GetInt64());
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("building")]
    [InlineData("abandoned")]
    public void OnlyConfirmedDepletedCellsMaySupplyEquipment(string status)
    {
        var (state, snapshot, catalog) = Fixture();
        state = state.With(state.Cells[0] with { Status = status });
        Assert.Empty(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed));
    }

    [Fact]
    public void AResumedNativeDrillInvalidatesTheHistoricalDepletedLabel()
    {
        var (state, snapshot, catalog) = Fixture();
        snapshot = Change(snapshot, "drill-work", new { statusName = "working" });
        Assert.Empty(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("process")]
    public void LoadedOrEngagedFurnacesStayStanding(string kind)
    {
        var (state, snapshot, catalog) = Fixture();
        snapshot = kind == "process" ? Change(snapshot, "furnace-work", new { inProcess = true })
            : Change(snapshot, kind, new { items = new Dictionary<string, int> { ["iron-plate"] = 1 } });
        Assert.Equal("drill", Assert.Single(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed)).Role);
    }

    [Theory]
    [InlineData("red", 1)]
    [InlineData("green", 1)]
    [InlineData("unknown", 0)]
    public void CircuitConnectionsAndMissingCircuitObservationPreventDismantling(string colour, int count)
    {
        var (state, snapshot, catalog) = Fixture();
        var circuit = colour == "unknown" ? new Dictionary<string, int>()
            : new Dictionary<string, int> { ["redNeighbourCount"] = colour == "red" ? count : 0,
                ["greenNeighbourCount"] = colour == "green" ? count : 0 };
        snapshot = Change(snapshot, "furnace", new { role = "factory", type = "furnace", position = new MapPosition(1, 0),
            inventories = new[] { "fuel", "input", "output" }, transport = circuit });
        Assert.Equal("drill", Assert.Single(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed)).Role);
    }

    [Fact]
    public void EquipmentAlreadyCarriedPreventsUnnecessaryDismantling()
    {
        var (state, snapshot, catalog) = Fixture();
        snapshot = Change(snapshot, "main", new { items = new Dictionary<string, int> { ["electric-mining-drill"] = 1, ["steel-furnace"] = 1 } });
        Assert.Empty(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed));
    }

    [Fact]
    public void SharedClaimsAndMovedMachinesCannotBeRecovered()
    {
        var (state, snapshot, catalog) = Fixture();
        state = state.With(state.Cells[0] with { Id = "shared", Entities = new Dictionary<string, string> { ["drill"] = "drill" } });
        snapshot = Change(snapshot, "furnace", new { role = "factory", type = "furnace", position = new MapPosition(2, 0),
            inventories = new[] { "fuel", "input", "output" }, transport = new { redNeighbourCount = 0, greenNeighbourCount = 0 } });
        Assert.Empty(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed));
    }

    [Fact]
    public void ARecoveredDrillDoesNotStrandTheRemainingIdleFurnace()
    {
        var (state, snapshot, catalog) = Fixture();
        var cell = state.Cells[0];
        state = state.With(cell with { Entities = cell.Entities.Where(p => p.Key != "drill").ToDictionary(p => p.Key, p => p.Value) });
        snapshot = snapshot with { Records = snapshot.Records.Where(r => r.EntityId != "drill").ToArray() };
        Assert.Equal("furnace", Assert.Single(ResourceEquipmentReuse.Candidates(state, snapshot, catalog, Needed)).Role);
    }

    [Fact]
    public void DistantCellsAndIncompatibleActorScopesCannotSupplyTheKit()
    {
        var (state, snapshot, catalog) = Fixture();
        var distant = Change(snapshot, "actor", new { role = "actor", mainInventoryId = "main", position = new MapPosition(200, 0) });
        Assert.Empty(ResourceEquipmentReuse.Candidates(state, distant, catalog, Needed));
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Candidates(state,
            snapshot with { Scope = snapshot.Scope with { Generation = snapshot.Scope.Generation + 1 } }, catalog, Needed));
    }

    [Fact]
    public void UnobservedInventoriesAndNonNormalStacksPreventMining()
    {
        var (state, snapshot, catalog) = Fixture();
        var missing = snapshot with { Records = snapshot.Records.Where(r => r.Id != "input").ToArray() };
        Assert.Equal("drill", Assert.Single(ResourceEquipmentReuse.Candidates(state, missing, catalog, Needed)).Role);
        var quality = Change(snapshot, "fuel", new { items = new Dictionary<string, int> { ["coal"] = 48 },
            stacks = new[] { new { name = "coal", quality = "uncommon", count = 48 } } });
        Assert.Equal("drill", Assert.Single(ResourceEquipmentReuse.Candidates(state, quality, catalog, Needed)).Role);
    }

    [Fact]
    public void FuelAndEquipmentMustJointlyFitRatherThanSharingTheSameLastFreeSlot()
    {
        var (_, snapshot, catalog) = Fixture();
        var part = new ReusableResourceEquipment("cell", "furnace", "steel-furnace", "furnace", new(1, 0));
        var incoming = ResourceEquipmentReuse.Incoming(snapshot, part);
        Assert.Equal(48, incoming["coal"]);
        Assert.Equal(1, incoming["steel-furnace"]);
        var hints = incoming.Keys.ToDictionary(item => item, _ => new { insertable = 50, canInsertOne = true, certainty = "native-estimate" });
        snapshot = Change(snapshot, "main", new { items = new Dictionary<string, int>(), slots = 1, usableSlots = 1,
            stacks = Array.Empty<object>(), filters = new { }, capacityHints = hints });
        Assert.False(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming));
        snapshot = Change(snapshot, "main", new { items = new Dictionary<string, int>(), slots = 2, usableSlots = 2,
            stacks = Array.Empty<object>(), filters = new { }, capacityHints = hints });
        Assert.True(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming));
    }

    [Fact]
    public void AFullNearestChestDoesNotHideAnAvailableProducerChestAndNeededStockIsKept()
    {
        var (state, snapshot, catalog) = Fixture();
        state = state.With(state.Cells[0] with { Id = "other", Entities = new Dictionary<string, string> { ["output-chest"] = "other-chest" } });
        snapshot = Change(snapshot, "main", new { items = new Dictionary<string, long> { ["iron-plate"] = 900, ["coal"] = 100 } });
        snapshot = snapshot with { Records = [.. snapshot.Records,
            new("chest", "entity", "chest", "iron-chest", Protocol.ToElement(new { role = "factory", position = new MapPosition(0, 0) })),
            new("other-chest", "entity", "other-chest", "iron-chest", Protocol.ToElement(new { role = "factory", position = new MapPosition(8, 0) })),
            new("other-stock", "inventory", "other-chest", "chest", Protocol.ToElement(new { items = new { }, capacityHints =
                new Dictionary<string, object> { ["iron-plate"] = new { insertable = 800, canInsertOne = true, certainty = "native-estimate" } } }))] };
        snapshot = Change(snapshot, "chest-stock", new { items = new { }, capacityHints =
            new Dictionary<string, object> { ["iron-plate"] = new { insertable = 0, canInsertOne = false, certainty = "native-estimate" } } });
        var home = Assert.Single(ResourceEquipmentReuse.DepositOptions(state, snapshot, catalog,
            new Dictionary<string, int> { ["iron-plate"] = 450 }));
        Assert.Equal("other-chest", home.EntityId);
        Assert.Equal(450, home.Count);
        Assert.Empty(ResourceEquipmentReuse.DepositOptions(state, snapshot, catalog,
            new Dictionary<string, int> { ["iron-plate"] = 900 }));
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.DepositOptions(state,
            snapshot with { Scope = snapshot.Scope with { Generation = 20 } }, catalog, Needed));
    }

    [Theory]
    [InlineData("completed", 28, null)]
    [InlineData("partial", 20, null)]
    [InlineData("failed", 0, "transfer_blocked")]
    public void TerminalMatchingTransfersAreMeasuredWithoutReplayingTheRemainder(string status, long moved, string? error)
    {
        var receipt = Receipt(status, moved, error);
        Assert.Equal(moved, ResourceEquipmentReuse.Transfer(receipt, "furnace", "coal", 28, "to_actor"));
    }

    [Theory]
    [InlineData("accepted", 0, null)]
    [InlineData("running", 20, null)]
    [InlineData("completed", 20, null)]
    [InlineData("partial", 28, null)]
    [InlineData("failed", 20, "transfer_blocked")]
    [InlineData("failed", 0, "target_lost")]
    public void UnknownOrInconsistentTransfersStillRequireReconciliation(string status, long moved, string? error)
    {
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Transfer(Receipt(status, moved, error), "furnace", "coal", 28, "to_actor"));
    }

    [Fact]
    public void ForeignTransferTargetItemDirectionAndRequestCannotCountAsRecovery()
    {
        var receipt = Receipt("completed", 28, null);
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Transfer(receipt, "other", "coal", 28, "to_actor"));
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Transfer(receipt, "furnace", "wood", 28, "to_actor"));
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Transfer(receipt, "furnace", "coal", 29, "to_actor"));
        Assert.Throws<InvalidDataException>(() => ResourceEquipmentReuse.Transfer(receipt, "furnace", "coal", 28, "from_actor"));
    }

    private static OperationReceipt Receipt(string status, long moved, string? error) => new("receipt", "take", status, 100, 100,
        Protocol.ToElement(new { targetId = "furnace", item = "coal", requested = 28, transferred = moved, direction = "to_actor" }),
        error is null ? null : new(error, "native refusal"), Protocol.ToElement(new { }));

    private static FactorySnapshot Change(FactorySnapshot snapshot, string id, object data) => snapshot with
    { Records = snapshot.Records.Select(r => r.Id == id ? r with { Data = Protocol.ToElement(data) } : r).ToArray() };

    private static (FactoryState State, FactorySnapshot Snapshot, ProductionCatalog Catalog) Fixture()
    {
        var catalog = Catalogs.Raw();
        catalog = catalog with { Items = new Dictionary<string, NativeItem>(catalog.Items)
            { ["steel-furnace"] = new(0, 50, PlaceEntity: "steel-furnace", PlaceEntityType: "furnace") } };
        var roles = new Dictionary<string, string> { ["drill"] = "drill", ["furnace"] = "furnace", ["output-chest"] = "chest",
            ["output-inserter"] = "arm", ["pole"] = "pole" };
        var plan = roles.ToDictionary(p => p.Key, p => new PlannedEntity(p.Key, p.Key switch
            { "drill" => "electric-mining-drill", "furnace" => "steel-furnace", "output-chest" => "iron-chest",
                "output-inserter" => "inserter", _ => "small-electric-pole" }, new(p.Key == "furnace" ? 1 : 0, 0), 0));
        var cell = new FactoryCell("cell", 0, new(1, 0, true), "smelter", "electric-mining-drill", "iron-plate", roles,
            ResourceCellHealth.Depleted, 50, Plan: plan);
        FactoryRecord Record(string id, string kind, string entity, string name, object data) => new(id, kind, entity, name, Protocol.ToElement(data));
        var snapshot = new FactorySnapshot("snapshot", catalog.Scope, 100, 200, Protocol.ToElement(new { }), [
            Record("actor", "entity", "actor", "character", new { role = "actor", mainInventoryId = "main", position = new MapPosition(-3, 0) }),
            Record("main", "inventory", "actor", "main", new { items = new Dictionary<string, int>() }),
            Record("drill", "entity", "drill", "electric-mining-drill", new { role = "factory", type = "mining-drill", position = new MapPosition(0, 0),
                inventories = Array.Empty<string>(), transport = new { redNeighbourCount = 0, greenNeighbourCount = 0 } }),
            Record("furnace", "entity", "furnace", "steel-furnace", new { role = "factory", type = "furnace", position = new MapPosition(1, 0),
                inventories = new[] { "fuel", "input", "output" }, transport = new { redNeighbourCount = 0, greenNeighbourCount = 0 } }),
            Record("drill-work", "work", "drill", "native-mining", new { statusName = "no_minable_resources" }),
            Record("furnace-work", "work", "furnace", "machine-craft", new { inProcess = false }),
            Record("fuel", "inventory", "furnace", "fuel", new { items = new Dictionary<string, int> { ["coal"] = 48 } }),
            Record("input", "inventory", "furnace", "crafter_input", new { items = new Dictionary<string, int>() }),
            Record("output", "inventory", "furnace", "crafter_output", new { items = new Dictionary<string, int>() }),
            Record("chest-stock", "inventory", "chest", "chest", new { items = new Dictionary<string, int> { ["iron-plate"] = 100 } })]);
        return (new(1, catalog.Scope.WorldId, [], [cell]), snapshot, catalog);
    }
}
