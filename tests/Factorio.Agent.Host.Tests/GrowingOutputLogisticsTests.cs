using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class GrowingOutputLogisticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFeedsThePlannedBufferEvenWhenThisStageAlreadyExists(bool existing)
    {
        string directory = Directory.CreateTempSubdirectory("preparation-input-buffer-").FullName;
        try
        {
            var game = new OutputGame(500, 0, false, recipeInput: true);
            var cell = new FactoryCell("gear", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["input-chest"] = "input" }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>());
            var state = new FactoryState(1, game.Scope.WorldId, [], [cell]).WithTarget("iron-gear-wheel", 24);
            await new FactoryRegistry(directory).SaveAsync(state, default);
            var catalog = Catalogs.Raw();
            var stage = AutomationPlanner.Plan(catalog, "iron-gear-wheel", 24, FactoryDirector.MachineItems(catalog)).Stages.Single();
            var journal = new Journal();
            await new FactoryDirector(game, journal, directory).StartStageAsync(stage, catalog,
                existing ? new HashSet<string> { cell.Id } : new HashSet<string>(), default);
            Assert.Equal(480, game.InputStock);
            Assert.Equal(20, game.Carried);
            Assert.Equal(1, game.Submissions);
            Assert.Contains("factory-stage-startup", journal.Types);
            Assert.False(File.Exists(Path.Combine(directory, "factory-logistics-completion.json")));
            var retained = Assert.Single((await new FactoryRegistry(directory).LoadAsync(game.Scope.WorldId, default)).Cells);
            Assert.Equal(cell.Id, retained.Id);
            Assert.Equal(cell.Entities, retained.Entities);
            Assert.Equal("ready", retained.Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AnExistingPlasticStageReceivesItsPlannedCoalDuringPreparation()
    {
        string directory = Directory.CreateTempSubdirectory("preparation-plastic-buffer-").FullName;
        try
        {
            var catalog = OilCatalogs.Oil();
            var game = new OutputGame(500, 0, false, recipeInput: true, nativeCatalog: catalog, recipeItem: "coal");
            var cell = new FactoryCell("plastic", 0, new(0, 0, true), "fluid", "chemical-plant", "plastic-bar",
                new Dictionary<string, string> { ["input-chest"] = "input" }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>());
            var state = new FactoryState(1, game.Scope.WorldId, [], [cell]).WithTarget("plastic-bar", 30);
            await new FactoryRegistry(directory).SaveAsync(state, default);
            var stage = AutomationPlanner.Plan(catalog, "plastic-bar", 30, FactoryDirector.MachineItems(catalog),
                fluidMachineItems: FluidChainDirector.Machines(catalog)).Stages.Single(s => s.Recipe == "plastic-bar");
            await new FactoryDirector(game, new Journal(), directory).StartStageAsync(stage, catalog, new HashSet<string> { cell.Id }, default);
            Assert.Equal(150, game.InputStock);
            Assert.Equal(350, game.Carried);
            Assert.Equal(1, game.Submissions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ANewStageStartsWithoutVisitingOtherConsumersOrReplacingTheFullTourReceipt()
    {
        string directory = Directory.CreateTempSubdirectory("stage-startup-").FullName;
        try
        {
            var game = new OutputGame(500, 0, false, recipeInput: true)
            {
                ExtraCarried = new Dictionary<string, long> { ["automation-science-pack"] = 30 },
                ExtraRecords = [
                    new("other-input", "entity", "other-input", "iron-chest", Protocol.ToElement(new { role = "factory", type = "container", position = new MapPosition(25, 25) })),
                    new("other-input-stock", "inventory", "other-input", "chest", Protocol.ToElement(new { items = new { } })),
                    new("other-output", "entity", "other-output", "iron-chest", Protocol.ToElement(new { role = "factory", type = "container", position = new MapPosition(25, 27) })),
                    new("other-output-stock", "inventory", "other-output", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { ["automation-science-pack"] = 40 } })),
                    new("other-lab", "entity", "other-lab", "lab", Protocol.ToElement(new { role = "factory", type = "lab", position = new MapPosition(25, 29) }))]
            };
            var selected = new FactoryCell("gear", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["input-chest"] = "input" }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>());
            var other = selected with { Id = "other", Entities = new Dictionary<string, string> { ["input-chest"] = "other-input", ["output-chest"] = "other-output" } };
            var lab = selected with { Id = "lab", Kind = "lab", Recipe = null, Entities = new Dictionary<string, string> { ["machine"] = "other-lab" } };
            var state = new FactoryState(1, game.Scope.WorldId, [], [selected, other, lab]);
            await new FactoryRegistry(directory).SaveAsync(state, default);
            await new FactoryLogisticsCompletionStore(directory).RecordAsync(game.Scope, game.Tick, state, 5, false, default);
            string receiptPath = Path.Combine(directory, "factory-logistics-completion.json");
            string before = await File.ReadAllTextAsync(receiptPath);
            var journal = new Journal();
            var result = await new FactoryLogistics(game, journal, directory).ServiceAsync(5,
                minimumIntervalTicks: FactoryLogistics.BetweenGoalsFreshnessTicks, targetCellIds: new HashSet<string> { selected.Id });
            Assert.Equal(10, result.Supplied.GetValueOrDefault("iron-plate"));
            Assert.Empty(result.Collected);
            Assert.Single(result.Supplied);
            Assert.Equal(1, game.Submissions);
            Assert.Equal(30, game.ExtraCarried["automation-science-pack"]);
            Assert.Contains("factory-stage-logistics", journal.Types);
            Assert.DoesNotContain("factory-logistics-recent-tour", journal.Types);
            Assert.Equal(before, await File.ReadAllTextAsync(receiptPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true, "iron-gear-wheel", 480)]
    [InlineData(false, "iron-gear-wheel", 80)]
    [InlineData(true, null, 80)]
    [InlineData(true, "copper-cable", 10)]
    public async Task PlannedResearchBuffersCoverFastIntermediatesWithoutInflatingLegacyOrUnplannedCells(bool planned, string? target, int expected)
    {
        string directory = Directory.CreateTempSubdirectory("planned-input-buffers-").FullName;
        try
        {
            var game = new OutputGame(500, 0, false, recipeInput: true);
            var cell = new FactoryCell("gear", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["input-chest"] = "input" }, "ready", 100,
                Plan: new Dictionary<string, PlannedEntity>());
            var state = new FactoryState(1, game.Scope.WorldId, [], [cell]);
            if (target is not null) state = state.WithTarget(target, 24);
            await new FactoryRegistry(directory).SaveAsync(state, default);
            var result = await new FactoryLogistics(game, new Journal(), directory).ServiceAsync(usePlannedBuffers: planned);
            Assert.Equal(expected, result.Supplied.GetValueOrDefault("iron-plate"));
            Assert.Equal(expected, game.InputStock);
            Assert.Equal(500 - expected, game.Carried);
            Assert.Equal(1, game.Submissions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LaboratoryTourFreshnessStartsAfterTheFinalNativeScienceTransfer()
    {
        string directory = Directory.CreateTempSubdirectory("science-tour-completion-").FullName;
        try
        {
            var game = new OutputGame(30, 0, false, labScience: true) { TransferTicks = 600 };
            var labs = game.LabStocks.Keys.Select(id => new FactoryCell(id, 1, new(0, 0, true), "lab", "lab", null,
                new Dictionary<string, string> { ["machine"] = id }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>())).ToArray();
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [], labs), default);
            var journal = new Journal();
            var first = await new FactoryLogistics(game, journal, directory).ServiceAsync();
            Assert.Equal(1900, first.Tick);
            game.Tick++;
            await new FactoryLogistics(game, journal, directory).ServiceAsync(minimumIntervalTicks: FactoryLogistics.BetweenGoalsFreshnessTicks);
            Assert.Equal(3, game.Submissions);
            Assert.Contains("factory-logistics-recent-tour", journal.Types);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FuelPhaseReadsABoilerThatRanDryDuringLaboratoryTransfers()
    {
        string directory = Directory.CreateTempSubdirectory("fuel-after-science-").FullName;
        try
        {
            var game = new OutputGame(30, 0, false, labScience: true)
                { TransferTicks = 3600, BoilerFuel = 50, ExhaustBoilerAfterScience = true };
            var labs = game.LabStocks.Keys.Select(id => new FactoryCell(id, 1, new(0, 0, true), "lab", "lab", null,
                new Dictionary<string, string> { ["machine"] = id }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>())).ToArray();
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [], labs), default);
            var result = await new FactoryLogistics(game, new Journal(), directory).ServiceAsync();
            Assert.Equal(30, result.Supplied.GetValueOrDefault("automation-science-pack"));
            Assert.True(result.PowerStarved);
            Assert.Equal(12, result.Shortfall.GetValueOrDefault("coal"));
            Assert.Equal(3, game.Submissions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("scope", false)]
    [InlineData("factory", false)]
    [InlineData("buffer", false)]
    [InlineData("corrupt", false)]
    [InlineData("fuel", false)]
    [InlineData("missing", false)]
    public async Task BetweenGoalsReusesOnlyACompletedRecentTourAfterFreshMaintenance(string change, bool deferred)
    {
        string directory = Directory.CreateTempSubdirectory("recent-logistics-").FullName;
        try
        {
            var game = new OutputGame(0, 12, false) { TransferTicks = 100, BoilerFuel = 50 };
            var cell = new FactoryCell("producer", 1, new(0, 0, true), "assembler", "assembling-machine-1", null,
                new Dictionary<string, string> { ["output-chest"] = "chest" }, "ready", 100,
                Plan: new Dictionary<string, PlannedEntity>());
            var registry = new FactoryRegistry(directory);
            await registry.SaveAsync(new(1, game.Scope.WorldId, [], [cell]), default);
            var journal = new Journal();
            var first = await new FactoryLogistics(game, journal, directory).ServiceAsync();
            Assert.Equal(200, first.Tick);
            Assert.Equal(1, game.Submissions);
            game.CurrentStock = 20;
            game.TransferTicks = 0;
            game.Tick += change == "expired" ? 1801 : change == "future" ? -1 : 1;
            if (change == "scope") game.Generation++;
            if (change == "factory") await registry.SaveAsync((await registry.LoadAsync(game.Scope.WorldId, default)).WithTarget("iron-plate", 60), default);
            if (change == "corrupt") await File.WriteAllTextAsync(Path.Combine(directory, "factory-logistics-completion.json"), "{broken");
            if (change == "fuel") game.BoilerFuel = 0;
            if (change == "missing") game.ChestPresent = false;
            int observations = game.FactoryPhotographs;
            var second = await new FactoryLogistics(game, journal, directory).ServiceAsync(change == "buffer" ? 80 : 40,
                minimumIntervalTicks: FactoryLogistics.BetweenGoalsFreshnessTicks);
            Assert.True(game.FactoryPhotographs > observations);
            Assert.Equal(2, journal.Types.Count(t => t == "factory-maintenance"));
            Assert.Equal(deferred, journal.Types.Contains("factory-logistics-recent-tour"));
            Assert.Equal(deferred || change == "missing" ? 1 : 2, game.Submissions);
            Assert.Equal(deferred || change == "missing" ? 0 : 20, second.Collected.GetValueOrDefault("iron-plate"));
            if (change == "fuel") Assert.True(second.PowerStarved);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ScarceScienceFeedsAllRegisteredLabsInsteadOfFillingTheFirstStack()
    {
        string directory = Directory.CreateTempSubdirectory("science-sharing-").FullName;
        try
        {
            var game = new OutputGame(30, 0, false, labScience: true);
            var labs = game.LabStocks.Keys.Select(id => new FactoryCell(id, 1, new(0, 0, true), "lab", "lab", null,
                new Dictionary<string, string> { ["machine"] = id }, "ready", 100, Plan: new Dictionary<string, PlannedEntity>())).ToArray();
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [], labs), default);
            var result = await new FactoryLogistics(game, new Journal(), directory).ServiceAsync();
            Assert.Equal(30, result.Supplied.GetValueOrDefault("automation-science-pack"));
            Assert.Equal([13L, 13L, 14L], game.LabStocks.Values.Order());
            Assert.Equal(0, game.Carried);
            Assert.Equal(3, game.Submissions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0, 12, false, 12)]
    [InlineData(0, 12, true, 12)]
    [InlineData(190, 32, false, 10)]
    [InlineData(198, 1, false, 1)]
    public async Task CollectionUsesTheBoundedNativeTransferRatherThanAnOldOutputQuantity(int carried, int currentStock, bool lostReply, int expected)
    {
        // The real green chest held six at the cycle snapshot and ten on arrival. A six-item take stranded the rest
        // for another four-minute tour. Shrinking stocks and an almost full carrying allowance must also remain safe.
        string directory = Directory.CreateTempSubdirectory("growing-output-").FullName;
        try
        {
            var game = new OutputGame(carried, currentStock, lostReply);
            var cell = new FactoryCell("producer", 1, new(0, 0, true), "assembler", "assembling-machine-1", null,
                new Dictionary<string, string> { ["output-chest"] = "chest" }, "ready", 100,
                Plan: new Dictionary<string, PlannedEntity>());
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [], [cell]), default);
            var result = await new FactoryLogistics(game, new Journal(), directory).ServiceAsync();
            Assert.Equal(expected, result.Collected.GetValueOrDefault("iron-plate"));
            Assert.Equal(carried + expected, game.Carried);
            Assert.True(game.Carried <= 200);
            Assert.Equal(1, game.Submissions);
            Assert.Equal(lostReply ? 1 : 0, game.Queries);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token) { Types.Add(type); return Task.CompletedTask; }
    }

    /// <summary>Six items in the cycle photograph; a different native quantity after approach, with measured receipts.</summary>
    private sealed class OutputGame(int carried, int currentStock, bool lostReply, bool labScience = false, bool recipeInput = false,
        ProductionCatalog? nativeCatalog = null, string? recipeItem = null) : IGameClient
    {
        private readonly ProductionCatalog catalog = nativeCatalog ?? Catalogs.Raw();
        private readonly SpatialSnapshot map = FactoryMaps.Grass(8, labScience
            ? new[] { Lab("lab-0", 3.5, -.5), Lab("lab-1", -.5, 3.5), Lab("lab-2", 4.5, 4.5) }
            : [new(recipeInput ? "input" : "chest", "iron-chest", new(2.5, .5), new(new(2.15, .15), new(2.85, .85)), 0, "own")]);
        private object? receipt;
        private string? operationId;
        private string Item => labScience ? "automation-science-pack" : recipeItem ?? "iron-plate";
        public Dictionary<string, long> LabStocks { get; } = new() { ["lab-0"] = 10, ["lab-1"] = 0, ["lab-2"] = 0 };
        public ActorScope Scope => catalog.Scope with { Generation = Generation };
        public long Generation { get; set; } = Catalogs.Raw().Scope.Generation;
        public long Tick { get; set; } = 100;
        public long TransferTicks { get; set; }
        public int CurrentStock { get; set; } = currentStock;
        public int InputStock { get; private set; }
        public int? BoilerFuel { get; set; }
        public bool ExhaustBoilerAfterScience { get; set; }
        public bool ChestPresent { get; set; } = true;
        public int FactoryPhotographs { get; private set; }
        public int Carried { get; private set; } = carried;
        public int Submissions { get; private set; }
        public int Queries { get; private set; }
        public IReadOnlyList<FactoryRecord> ExtraRecords { get; init; } = [];
        public IReadOnlyDictionary<string, long> ExtraCarried { get; init; } = new Dictionary<string, long>();

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            object data;
            switch (request.Action)
            {
                case "production_catalog": data = catalog with { Scope = Scope, CollectedTick = Tick }; break;
                case "spatial": data = map with { Scope = Scope, CollectedTick = Tick }; break;
                case "observe":
                    data = new
                    {
                        scope = Scope, collectedTick = Tick,
                        coverage = new { atomic = true, collectionStartTick = Tick, collectionEndTick = Tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>()
                    };
                    break;
                case "factory_snapshot":
                    FactoryPhotographs++;
                    var records = new List<FactoryRecord>
                    {
                        Record("actor", "entity", "actor", new { role = "actor", type = "character", position = map.Actor.Position, mainInventoryId = "bag" }),
                        Record("bag", "inventory", "actor", new { items = new Dictionary<string, long>(ExtraCarried) { [Item] = Carried } })
                    };
                    if (labScience)
                        foreach (var lab in map.Entities)
                        {
                            records.Add(Record(lab.Id, "entity", lab.Id, new { role = "factory", type = "lab", position = lab.Position }));
                            records.Add(Record(lab.Id + "-stock", "inventory", lab.Id, new { items = new Dictionary<string, long> { [Item] = LabStocks[lab.Id] } }));
                        }
                    else if (recipeInput)
                    {
                        records.Add(Record("input", "entity", "input", new { role = "factory", type = "container", position = new MapPosition(2.5, .5) }));
                        records.Add(Record("input-stock", "inventory", "input", new { items = new Dictionary<string, long> { [Item] = InputStock } }));
                    }
                    else if (ChestPresent)
                    {
                        records.Add(Record("chest", "entity", "chest", new { role = "factory", type = "container", position = new MapPosition(2.5, .5) }));
                        records.Add(Record("stock", "inventory", "chest", new { items = new Dictionary<string, long> { [Item] = Submissions == 0 ? 6 : CurrentStock } }));
                    }
                    if (BoilerFuel is { } fuel)
                    {
                        records.Add(Record("boiler", "entity", "boiler", new { role = "factory", type = "boiler", fuelInventoryId = "burner", position = new MapPosition(5, 5) }));
                        records.Add(Record("burner", "inventory", "boiler", new { items = new Dictionary<string, long> { ["coal"] = fuel } }));
                    }
                    records.AddRange(ExtraRecords);
                    data = new { snapshotId = "stock", scope = Scope, snapshotScope = Scope, collectedTick = Tick, expiresTick = Tick + 1000,
                        totalRecords = records.Count, offset = 0, nextOffset = records.Count, complete = true,
                        coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }, records };
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Assert.Equal(labScience || recipeInput ? "insert" : "take", submission.Kind);
                    string target = submission.Args.GetProperty("entityId").GetString()!;
                    if (labScience) Assert.Contains(target, LabStocks.Keys);
                    else Assert.Equal(recipeInput ? "input" : "chest", target);
                    Assert.Equal(Item, submission.Args.GetProperty("item").GetString());
                    Assert.Equal(Scope, submission.Scope);
                    Submissions++;
                    operationId = submission.OperationId;
                    int requested = submission.Args.GetProperty("count").GetInt32();
                    int moved = Math.Min(requested, labScience || recipeInput ? Carried : CurrentStock);
                    Carried += labScience || recipeInput ? -moved : moved;
                    if (labScience) LabStocks[target] += moved;
                    else if (recipeInput) InputStock += moved;
                    else CurrentStock -= moved;
                    Tick += TransferTicks;
                    if (labScience && ExhaustBoilerAfterScience && Submissions == LabStocks.Count) BoilerFuel = 0;
                    receipt = new { submission.OperationId, submission.Kind, status = moved == requested ? "completed" : "partial",
                        acceptedTick = Tick - TransferTicks, updatedTick = Tick, effects = new { requested, transferred = moved } };
                    if (lostReply) throw new IOException("The transfer occurred but its response was lost.");
                    data = receipt;
                    break;
                case "operation":
                    Assert.Equal(operationId, request.Arguments.GetProperty("operationId").GetString());
                    Queries++;
                    data = receipt!;
                    break;
                default: throw new InvalidOperationException($"Unexpected call: {request.Action}");
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Tick, Protocol.ToElement(data)));
        }

        private static FactoryRecord Record(string id, string kind, string entity, object data) => new(id, kind, entity, id, Protocol.ToElement(data));
        private static SpatialEntity Lab(string id, double x, double y) => new(id, "lab", new(x, y), new(new(x - 1.2, y - 1.2), new(x + 1.2, y + 1.2)), 0, "own");
    }
}
