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
    public void AnExplicitResumeCannotFallBackToAnotherCellOfTheSameProduct()
    {
        var row = Row(FactoryMaps.Grass(30), Smelter) with { Cells = 2 };
        var dangerous = Cell(row, 0, "building", new()) with { Attempts = 1 };
        var safe = Cell(row, 1, "building", new()) with { Attempts = 1 };
        var state = new FactoryState(1, "world", [], [dangerous, safe], [row]);
        Assert.Equal(safe.Id, ResourceCellBuilder.Interrupted(state, row.Kind, row.Product, safe.Id).Resume?.Id);
        Assert.Throws<InvalidOperationException>(() => ResourceCellBuilder.Interrupted(state, row.Kind, row.Product, "unknown"));
        Assert.Throws<InvalidOperationException>(() => ResourceCellBuilder.Interrupted(state with
        {
            Cells = [dangerous, safe with { Attempts = ResourceCellBuilder.MaximumAttempts }]
        }, row.Kind, row.Product, safe.Id));
    }

    [Fact]
    public async Task TheDirectorDefersAnUnsafeLegacyCellAndPassesTheSafeCellIdToTheBuilder()
    {
        string directory = Directory.CreateTempSubdirectory("resource-resume-").FullName;
        try
        {
            var map = FactoryMaps.Grass(30) with { Scope = Scope };
            var dangerousRow = Row(map, Smelter);
            var safeRow = dangerousRow with { Id = 2, Origin = new(100, 0) };
            var dangerous = Cell(dangerousRow, 0, "building", new()) with { Attempts = 1 };
            var safe = Cell(safeRow, 0, "building", new()) with { Attempts = 1 };
            await new FactoryRegistry(directory).SaveAsync(new(1, "world", [], [dangerous, safe], [dangerousRow, safeRow]), CancellationToken.None);
            var journal = new PlanJournal();
            var game = new ResumeGame(map, new(1, 10, 17, 1, new(0, 0)));
            Assert.Equal(0, await new FactoryDirector(game, journal, directory).ResumeResourceCellsAsync(Scope, CancellationToken.None));
            Assert.True(journal.Plans.SequenceEqual([safe.Id]), string.Join("; ", journal.Errors));
            Assert.DoesNotContain("submit", game.Actions); // Stop before procurement or movement in this regression.
            var state = await new FactoryRegistry(directory).LoadAsync("world", CancellationToken.None);
            Assert.Equal(1, state.Cells.Single(c => c.Id == dangerous.Id).Attempts);
            Assert.Equal(2, state.Cells.Single(c => c.Id == safe.Id).Attempts);
            Assert.All(state.Cells, c => Assert.Equal(5, c.Plan!.Count));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void APartialLegacyPlanCannotHideADrillInsideADeathZone()
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, Smelter);
        var layout = new ResourceCellPlanner().Layout(map, row, 0);
        var cell = Cell(row, 0, "building", new());
        var linked = FactoryCellBuilder.WithLink(cell, "link", new(new(100.5, 100.5), 0, 0), "small-electric-pole");
        var death = new NativeDeathTransition(1, 10, 17, 1, layout.Role("drill")!.Position);
        Assert.False(ResourceCellBuilder.Safe(cell, []));
        Assert.False(ResourceCellBuilder.Safe(linked, [death]));
        var recovered = ResourceCellBuilder.WithLayout(linked, layout);
        Assert.Equal(layout.Entities.Count + 1, recovered.Plan!.Count);
        Assert.Equal(linked.Plan!["link-0"], recovered.Plan["link-0"]);
        Assert.False(ResourceCellBuilder.Safe(recovered, [death]));
        Assert.True(ResourceCellBuilder.Safe(recovered, []));
    }

    [Fact]
    public void MaintenanceCannotBypassTheDeathZoneDeferralOfAReadyResourceCell()
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, Smelter);
        var layout = new ResourceCellPlanner().Layout(map, row, 0);
        var cell = ResourceCellBuilder.WithLayout(Cell(row, 0, "ready", new()), layout);
        var death = new NativeDeathTransition(1, 10, 17, 1, layout.Role("drill")!.Position);
        Assert.True(FactoryMaintenance.DeferResourceRebuild(cell, [death]));
        Assert.True(FactoryMaintenance.DeferResourceRebuild(cell with { Plan = null }, [death]));
        Assert.False(FactoryMaintenance.DeferResourceRebuild(cell, []));
        Assert.False(FactoryMaintenance.DeferResourceRebuild(cell, [death with { Position = new(100, 100) }]));
        Assert.False(FactoryMaintenance.DeferResourceRebuild(cell with { Kind = "turret" }, [death]));
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

    private sealed class ResumeGame(SpatialSnapshot map, NativeDeathTransition death) : IGameClient, IDangerZoneReader
    {
        public List<string> Actions { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            return request.Action switch
            {
                "production_catalog" => Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick,
                    Protocol.ToElement(Catalogs.Raw() with { Scope = Scope, CollectedTick = map.CollectedTick }))),
                "spatial" => Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(map))),
                _ => throw new InvalidOperationException("Regression stops before any game mutation.")
            };
        }
        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick, CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<NativeDeathTransition>>([death]);
    }

    private sealed class PlanJournal : IControllerJournal
    {
        public List<string> Plans { get; } = [];
        public List<string> Errors { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "resource-cell-plan") Plans.Add(Protocol.ToElement(data).GetProperty("id").GetString()!);
            if (type == "resource-cell-resume-failed") Errors.Add(Protocol.ToElement(data).GetProperty("message").GetString()!);
            return Task.CompletedTask;
        }
    }
}
