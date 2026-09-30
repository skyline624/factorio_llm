using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceCellRecoveryTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
    private static readonly ResourceCellEquipment Smelter = new("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
    private static readonly ResourceCellEquipment Miner = new("electric-mining-drill", "iron-chest", Pole: "small-electric-pole");

    [Fact]
    public async Task LinkPolesAreProcuredForTheWholePlannedChainFromCarriedStock()
    {
        // A cell pole about twenty tiles from the only powered pole: a chain of several small poles joins them.
        var next = new PowerGridPlanner().Next(PowerGridPlannerTests.Map(), "pole-item", new(new(19, 0), new(22, 3)),
            new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Extension, next.Status);
        int chain = PowerGridPlanner.ChainPoles(next);
        Assert.InRange(chain, 3, 8);
        Assert.Equal(0, PowerGridPlanner.ChainPoles(new(PowerGridSearchStatus.Connected, "source")));

        // The cell's own pole is placed and one spare is carried: the whole chain is still procured before the first link.
        var executor = new RecordingExecutor();
        await CarriedStock.EnsureAsync(executor, new Dictionary<string, long> { ["pole-item"] = 1 }, "pole-item", chain, CancellationToken.None);
        Assert.Equal([("pole-item", chain)], executor.Requests);
        await CarriedStock.EnsureAsync(executor, new Dictionary<string, long> { ["pole-item"] = chain }, "pole-item", chain, CancellationToken.None);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public void OnlyUnrecordedLayoutPartsAreProcuredForACell()
    {
        var map = FactoryMaps.Grass(30);
        var layout = new ResourceCellPlanner().Layout(map, Row(map, Smelter), 0);
        var unplaced = CarriedStock.Unplaced(layout, new Dictionary<string, string> { ["pole"] = "10", ["output-chest"] = "11" });
        Assert.Equal(new Dictionary<string, int> { ["electric-mining-drill"] = 1, ["stone-furnace"] = 1, ["inserter"] = 1 }, unplaced);
    }

    [Fact]
    public void InterruptedCellsResumeWithinTheirAttemptBudgetAndAreAbandonedAfterIt()
    {
        var iron = Row(FactoryMaps.Grass(30), Smelter) with { Cells = 3 };
        var worn = Cell(iron, 0, "building", new()) with { Attempts = ResourceCellBuilder.MaximumAttempts };
        var fresh = Cell(iron, 1, "building", new()) with { Attempts = 1 };
        var state = new FactoryState(1, "world", [], [worn, fresh], [iron]);
        var (resume, abandoned) = ResourceCellBuilder.Interrupted(state, "smelter", "iron-plate");
        Assert.Equal(fresh with { Attempts = 2 }, resume);
        Assert.Equal([worn with { Status = ResourceCellBuilder.Abandoned }], abandoned);
        var none = ResourceCellBuilder.Interrupted(state with { Cells = [worn] }, "smelter", "iron-plate");
        Assert.Null(none.Resume);
        Assert.Single(none.Abandoned);
        Assert.Null(ResourceCellBuilder.Interrupted(state, "miner", "coal").Resume);
    }

    [Fact]
    public void ResumeDropsRecordedPartsThatNoLongerStandAtTheirPlannedPosition()
    {
        var map = FactoryMaps.Grass(30);
        var layout = new ResourceCellPlanner().Layout(map, Row(map, Smelter), 0);
        var recorded = new Dictionary<string, string> { ["pole"] = "10", ["output-chest"] = "11", ["furnace"] = "12", ["drill"] = "13" };
        var snapshot = Snapshot(
            Entity("10", "electric-pole", layout.Role("pole")!.Position),
            // "11" was destroyed; "12" answers to the id but not at the furnace tile.
            Entity("12", "furnace", new(layout.Role("furnace")!.Position.X + 3, layout.Role("furnace")!.Position.Y)),
            Entity("13", "mining-drill", layout.Role("drill")!.Position));
        Assert.Equal(new Dictionary<string, string> { ["pole"] = "10", ["drill"] = "13" }, ResourceCellHealth.Standing(snapshot, recorded, layout));
        Assert.Equal(new Dictionary<string, string> { ["pole"] = "10", ["furnace"] = "12", ["drill"] = "13" },
            ResourceCellHealth.Standing(snapshot, recorded));
    }

    [Fact]
    public void DestroyedPartsReopenAReadyCellForRepairAndExhaustedDrillsRetireIt()
    {
        var map = FactoryMaps.Grass(30);
        var iron = Row(map, Smelter);
        var coal = Row(map, Miner) with { Id = 2, Kind = "miner", Product = "coal", Resource = "coal", CellPerMinute = 30, Cells = 2 };
        var damaged = Cell(iron, 0, "ready", new() { ["pole"] = "1", ["output-chest"] = "2", ["output-inserter"] = "3", ["furnace"] = "4", ["drill"] = "5" });
        var exhausted = Cell(coal, 0, "ready", new() { ["pole"] = "6", ["output-chest"] = "7", ["drill"] = "8" });
        var unpowered = Cell(coal, 1, "ready", new() { ["pole"] = "9", ["output-chest"] = "10", ["drill"] = "11" });
        var assembler = new FactoryCell("gears", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
            new Dictionary<string, string> { ["machine"] = "99" }, "ready", 1);
        var at = new MapPosition(0, 0);
        var snapshot = Snapshot(Entity("1", "electric-pole", at), Entity("3", "inserter", at), Entity("4", "furnace", at), Entity("5", "mining-drill", at),
            Entity("6", "electric-pole", at), Entity("7", "container", at), Entity("8", "mining-drill", at), Work("8", "no_minable_resources"),
            Entity("9", "electric-pole", at), Entity("10", "container", at), Entity("11", "mining-drill", at), Work("11", "no_power"));

        var changed = ResourceCellHealth.Inspect(snapshot, [damaged, exhausted, unpowered, assembler]);

        // A browned-out network is not a cell defect: only the missing chest and the exhausted deposit change a status.
        Assert.Equal(2, changed.Count);
        var reopened = changed.Single(c => c.Id == damaged.Id);
        Assert.Equal("building", reopened.Status);
        Assert.Equal(0, reopened.Attempts);
        Assert.Equal(["drill", "furnace", "output-inserter", "pole"], reopened.Entities.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ResourceCellHealth.Depleted, changed.Single(c => c.Id == exhausted.Id).Status);
        var state = new FactoryState(1, "world", [], [damaged, exhausted, unpowered], [iron, coal]);
        state = changed.Aggregate(state, (current, cell) => current.With(cell));
        Assert.Equal((0, 0.0), FactoryDirector.RawCapacity(state, "iron-plate"));
        Assert.Equal((1, 30.0), FactoryDirector.RawCapacity(state, "coal"));
    }

    [Fact]
    public void GrowthFailuresFallBackToProcurementButOuterCancellationAborts()
    {
        using var outer = new CancellationTokenSource();
        Assert.True(FactoryResearchController.Recoverable(new InvalidDataException("The drill does not drop into its planned receiver."), outer.Token));
        Assert.True(FactoryResearchController.Recoverable(new InvalidOperationException(), outer.Token));
        Assert.True(FactoryResearchController.Recoverable(new TimeoutException(), outer.Token));
        // The builder's own deadline cancels only its linked token.
        Assert.True(FactoryResearchController.Recoverable(new TaskCanceledException(), outer.Token));
        Assert.False(FactoryResearchController.Recoverable(new ArgumentException(), outer.Token));
        outer.Cancel();
        Assert.False(FactoryResearchController.Recoverable(new OperationCanceledException(outer.Token), outer.Token));
    }

    private static ResourceRow Row(SpatialSnapshot map, ResourceCellEquipment equipment) =>
        new(1, "smelter", "iron-plate", "iron-ore", equipment, new(0, 0), 0,
            ResourceCellPlanner.Pitch(map, equipment, 0) ?? throw new InvalidDataException("No template."), 1, 18.75);

    private static FactoryCell Cell(ResourceRow row, int index, string status, Dictionary<string, string> entities) =>
        new($"cell-{row.Id}-{index}", 0, new(row.Id, index, true), row.Kind, row.Equipment.Drill, row.Product, entities, status, 1);

    private static FactorySnapshot Snapshot(params FactoryRecord[] records) =>
        new("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), records);

    private static FactoryRecord Entity(string id, string type, MapPosition position) =>
        new(id, "entity", id, type, Protocol.ToElement(new { role = "factory", type, position }));

    private static FactoryRecord Work(string drill, string statusName) =>
        new($"work:{drill}", "work", drill, "native-mining", Protocol.ToElement(new { status = 1, statusName }));

    private sealed class RecordingExecutor : IStockGoalExecutor
    {
        public List<(string Item, int Target)> Requests { get; } = [];

        public Task<StockGoalResult> RunAsync(string item, int targetStock, CancellationToken token = default)
        {
            Requests.Add((item, targetStock));
            return Task.FromResult(new StockGoalResult("recorded", item, targetStock, 0, targetStock, 0, 0));
        }
    }
}
