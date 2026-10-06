using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SupplyLinesTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
    private static readonly FactoryZone Band = new(1, new(0, 0), 8, 3, 12);

    [Fact]
    public void LogisticsEmptiesTheDepotInsteadOfTheRowChestsAPoweredLineServes()
    {
        // Campaign 2026-10-01 (seed 20261002): every round walked to dozens of row chests far west of the bands.
        FactoryCell[] cells =
        [
            Smelter(3, 0, "c0"), Smelter(3, 1, "c1"), Smelter(4, 0, "c2"), Gears(),
            Line(3, "ready", new() { ["feeder-0"] = "f0", ["feeder-1"] = "f1", ["depot-inserter"] = "arm", ["output-chest"] = "depot", ["collector-000"] = "b0" })
        ];
        // The feeder of cell 1 sits on an island without a generator: that cell's chest is still collected by hand.
        var snapshot = Power(("source", 1, true), ("f0", 1, false), ("f1", 2, false), ("arm", 1, false),
            ("depot", 1, false), ("b0", 1, false));
        Assert.Equal(["c1", "c2", "gears", "depot"], FactoryLogistics.CollectedChests(cells, snapshot));
        // Without a fed depot inserter nothing reaches the depot: the whole row is collected directly.
        Assert.Equal(["c0", "c1", "c2", "gears", "depot"], FactoryLogistics.CollectedChests(cells,
            Power(("source", 1, true), ("f0", 1, false), ("f1", 1, false), ("arm", 2, false), ("depot", 1, false), ("b0", 1, false))));
        // A line still being built serves nothing.
        cells[^1] = cells[^1] with { Status = "building" };
        Assert.Equal(["c0", "c1", "c2", "gears", "depot"], FactoryLogistics.CollectedChests(cells, snapshot));
    }

    [Fact]
    public void DestroyedTrunkReturnsItsRowToDirectCollection()
    {
        FactoryCell[] cells = [Smelter(3, 0, "c0"), Line(3, "ready", new()
        {
            ["feeder-0"] = "f0", ["depot-inserter"] = "arm", ["output-chest"] = "depot", ["trunk-000"] = "missing-belt"
        })];
        var snapshot = Power(("source", 1, true), ("f0", 1, false), ("arm", 1, false), ("depot", 1, false));
        Assert.Equal(["c0", "depot"], FactoryLogistics.CollectedChests(cells, snapshot));
    }

    [Fact]
    public void RetiredRowStockRemainsCollectableAlongsideItsPoweredDepot()
    {
        FactoryCell[] cells = [Smelter(3, 0, "retired-stock", "depleted"), Line(3, "ready", new()
        {
            ["feeder-0"] = "f0", ["depot-inserter"] = "arm", ["output-chest"] = "depot"
        })];
        var snapshot = Power(("source", 1, true), ("f0", 1, false), ("arm", 1, false), ("depot", 1, false));
        Assert.Equal(["retired-stock", "depot"], FactoryLogistics.CollectedChests(cells, snapshot));
    }

    [Fact]
    public void LineRolesKeepTheirFlowOrderAndFeedersNameTheirCells()
    {
        string[] roles = ["trunk-001", "collector-002", "trunk-000", "collector-010", "feeder-0", "feeder-12", "feeder-x", "output-chest", "link-0"];
        Assert.Equal(["collector-002", "collector-010", "trunk-000", "trunk-001"], SupplyLines.FlowRoles(roles));
        Assert.Equal([0, 12], SupplyLines.FeederCells(roles));
        Assert.True(SupplyLines.IsTrunk(SupplyLinePlanner.TrunkRole(7)));
        Assert.False(SupplyLines.IsTrunk(SupplyLinePlanner.CollectorRole(7)));
    }

    [Fact]
    public void OnlyIngredientsOfReadyBandCellsAreConsumed()
    {
        var state = new FactoryState(1, "world", [Band], [Gears(), Smelter(1, 0, "c"), Gears() with { Id = "cables", Recipe = "copper-cable", Status = "building" }]);
        Assert.Equal(["iron-plate"], SupplyLines.Consumed(Catalogs.Early(), state));
    }

    [Fact]
    public void ADistantRowWhosePlatesTheBandsConsumeGetsTheNextLine()
    {
        var chests = Chests();
        var state = State([Gears(), Smelter(1, 0, "a"), Smelter(1, 1, "b", "building"), Smelter(2, 0, "copper"), Smelter(3, 0, "near")]);
        var need = SupplyLines.Next(state, new HashSet<string> { "iron-plate" }, chests)!;
        // Row 2 makes copper nobody consumes; row 3 is within reach of the walkway (x 0..24, y 5..7). Only ready cells count.
        Assert.Equal((1, "new", 1, 50.5), (need.Row, need.Reason, need.ReadyCells, need.Distance));
        Assert.Null(SupplyLines.Next(state with { Zones = [] }, new HashSet<string> { "iron-plate" }, chests));
        Assert.Null(SupplyLines.Next(state, new HashSet<string> { "steel-plate" }, chests));
    }

    [Fact]
    public void InterruptedLinesResumeFirstThenReadyLinesGainFeedersForNewCells()
    {
        var chests = Chests();
        var plan = new Dictionary<string, PlannedEntity> { ["feeder-0"] = new("feeder-0", "inserter", new(-50.5, 7.5), 0),
            ["feeder-1"] = new("feeder-1", "inserter", new(-47.5, 7.5), 0) };
        var ready = Line(1, "ready", new() { ["feeder-0"] = "f0" }) with { Plan = plan };
        var cells = new List<FactoryCell> { Gears(), Smelter(1, 0, "a"), Smelter(1, 1, "b"), ready };
        Assert.Equal("extend", SupplyLines.Next(State([.. cells]), new HashSet<string> { "iron-plate" }, chests)!.Reason);
        // With both feeders built there is nothing left to do for the row.
        cells[^1] = ready with { Entities = new Dictionary<string, string> { ["feeder-0"] = "f0", ["feeder-1"] = "f1" } };
        Assert.Null(SupplyLines.Next(State([.. cells]), new HashSet<string> { "iron-plate" }, chests));
        // An interrupted line goes before a new one, an abandoned line is left alone.
        cells[^1] = ready with { Status = "building" };
        cells.AddRange([Smelter(4, 0, "far"), Smelter(4, 1, "farther")]);
        var resumed = SupplyLines.Next(State([.. cells]), new HashSet<string> { "iron-plate" }, chests)!;
        Assert.Equal((1, "resume", "supply-1"), (resumed.Row, resumed.Reason, resumed.Line));
        cells[3] = ready with { Status = SupplyLineBuilder.Abandoned };
        var next = SupplyLines.Next(State([.. cells]), new HashSet<string> { "iron-plate" }, chests)!;
        Assert.Equal((4, "new", 2), (next.Row, next.Reason, next.ReadyCells));
    }

    [Fact]
    public void ATravelMeasureSumsEachMoveFromItsSubmissionToItsReceiptAndCountsTransfers()
    {
        JsonElement Row(string type, object data) => Protocol.ToElement(new { type, data });
        JsonElement[] rows =
        [
            Row("submission", new { operationId = "m1", kind = "move", preconditions = new { position = new MapPosition(0, 0) } }),
            Row("receipt", new { operationId = "m1", effects = new { position = new MapPosition(3, 4) } }),
            Row("submission", new { operationId = "t1", kind = "take", args = new { item = "iron-plate" }, preconditions = new { } }),
            Row("receipt", new { operationId = "t1", effects = new { position = new MapPosition(3, 4) } }),
            Row("submission", new { operationId = "m2", kind = "move", preconditions = new { position = new MapPosition(3, 4) } }),
            Row("receipt", new { operationId = "m2", effects = new { position = new MapPosition(3, 10) } }),
            Row("submission", new { operationId = "t2", kind = "take", args = new { item = "iron-gear-wheel" }, preconditions = new { } }),
            Row("submission", new { operationId = "i1", kind = "insert", args = new { item = "iron-plate" }, preconditions = new { } })
        ];
        // Two moves walk 5 and 6 tiles; one of the three transfers takes plates.
        Assert.Equal(new SupplyLineQualification.Travel(11, 2, 1, 3), SupplyLineQualification.Measure(rows));
    }

    [Fact]
    public void TheLedgerBalancesFinishedPlatesAgainstFurnaceOutputsChestsTransitAndTheDepot()
    {
        var smelter = new FactoryCell("s", 0, new(1, 0, true), "smelter", "electric-mining-drill", "iron-plate",
            new Dictionary<string, string> { ["furnace"] = "f", ["output-chest"] = "c", ["output-inserter"] = "o", ["drill"] = "d" }, "ready", 1);
        var line = new FactoryCell("l", 0, new(1, 0, true), SupplyLinePlanner.Kind, "transport-belt", "iron-plate", new Dictionary<string, string>
        {
            ["feeder-0"] = "fe", ["collector-000"] = "b", ["depot-inserter"] = "di", ["output-chest"] = "depot", ["link-0"] = "p"
        }, "ready", 1);
        static JsonElement Plates(long count) => Protocol.ToElement(new { items = new Dictionary<string, long> { ["iron-plate"] = count } });
        FactorySnapshot Photograph(long finished, long furnace, long chest, long belt, long hand, long depot) => new("snapshot", Scope, 1, 2,
            Protocol.ToElement(new { }), [
                new("work:f", "work", "f", "machine-craft", Protocol.ToElement(new { productsFinished = finished })),
                new("inventory:f:3", "inventory", "f", "furnace_result", Plates(furnace)),
                new("inventory:c:1", "inventory", "c", "chest", Plates(chest)),
                new("transport:b:1", "transit", "b", "transport-line", Plates(belt)),
                new("held:fe", "transit", "fe", "inserter-hand", Plates(hand)),
                new("inventory:depot:1", "inventory", "depot", "chest", Plates(depot))]);
        // Eight plates finished: one left the furnace, three left the chest, seven more ride the line and five reached the depot.
        Assert.Equal(new SupplyLineQualification.LineLedger(8, 8, 5, 7, -3),
            SupplyLineQualification.Ledger(Photograph(10, 1, 5, 3, 0, 0), Photograph(18, 0, 2, 9, 1, 5), [smelter], line));
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<MapPosition>> Chests() => new Dictionary<int, IReadOnlyList<MapPosition>>
    {
        [1] = [new(-50.5, 6.5), new(-47.5, 6.5)], [2] = [new(-60.5, 6.5)], [3] = [new(-10.5, 6.5)], [4] = [new(-80.5, 6.5), new(-77.5, 6.5)]
    };

    private static FactoryState State(FactoryCell[] cells) => new(1, "world", [Band], cells,
        [RowOf(1, "iron-plate"), RowOf(2, "copper-plate"), RowOf(3, "iron-plate"), RowOf(4, "iron-plate")]);

    private static ResourceRow RowOf(int id, string product) => new(id, "smelter", product, product.Replace("plate", "ore"),
        new("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole"), new(0, 0), 0, 3, 2, 18.75);

    private static FactoryCell Smelter(int row, int index, string chest, string status = "ready") =>
        new($"smelter-{row}-{index}", 0, new(row, index, true), "smelter", "electric-mining-drill", "iron-plate",
            new Dictionary<string, string> { ["furnace"] = chest + "-furnace", ["output-chest"] = chest }, status, 1);

    private static FactoryCell Gears() => new("gears", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
        new Dictionary<string, string> { ["machine"] = "m", ["input-chest"] = "in", ["output-chest"] = "gears" }, "ready", 1);

    private static FactoryCell Line(int row, string status, Dictionary<string, string> entities) =>
        new($"supply-{row}", 0, new(row, 0, true), SupplyLinePlanner.Kind, "transport-belt", "iron-plate", entities, status, 1,
            Plan: entities.ToDictionary(e => e.Key, e => new PlannedEntity(e.Key, "transport-belt", new(0, 0), 0)));

    /// <summary>Electric entities by network; a power source makes its network fed.</summary>
    private static FactorySnapshot Power(params (string Id, long Network, bool Source)[] entities) => new("snapshot", Scope, 100, 200,
        Protocol.ToElement(new { }), entities.Select(e => new FactoryRecord(e.Id, "entity", e.Id, e.Id, Protocol.ToElement(new
        {
            role = "factory", type = e.Source ? "electric-energy-interface" : "inserter", power = new { energy = 1.0, networkId = e.Network }
        }))).ToArray());
}
