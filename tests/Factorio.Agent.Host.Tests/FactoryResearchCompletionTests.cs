using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryResearchCompletionTests
{
    [Fact]
    public async Task CompletedResearchDoesNotProcureTheShortfallLeftByItsLastLogisticsRound()
    {
        string directory = Directory.CreateTempSubdirectory("research-completion-").FullName;
        try
        {
            var game = new FinishingGame();
            var producer = Cell("producer", "assembler", "assembling-machine-1", "automation-science-pack",
                new() { ["machine"] = "machine", ["input-chest"] = "input" });
            var lab = Cell("laboratory", "lab", "lab", null, new() { ["machine"] = "lab" });
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [new(1, new(0, 0), 4, 6, 6)], [producer, lab]), default);
            var journal = new Journal();

            var result = await new FactoryResearchController(game, journal, directory).RunAsync("target");

            Assert.Equal(1, result.Rounds);
            Assert.Empty(result.Procured);
            Assert.Equal(1, game.Transfers);
            Assert.True(game.ResearchReads >= 2);
            Assert.Equal(10, journal.Shortfall.GetValueOrDefault("prepared-substrate"));
            Assert.Contains("factory-research-result", journal.Types);
        }
        finally { Directory.Delete(directory, true); }

        static FactoryCell Cell(string id, string kind, string machine, string? recipe, Dictionary<string, string> entities) =>
            new(id, 1, new(0, 0, true), kind, machine, recipe, entities, "ready", 100, Plan: new Dictionary<string, PlannedEntity>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LoadedLabsStartBeforePreparationAndStopFurtherWorkAfterNativeCompletion(int photosBeforeCompletion)
    {
        string directory = Directory.CreateTempSubdirectory("research-overlap-").FullName;
        try
        {
            var game = new FinishingGame(photosBeforeCompletion, "stale");
            FactoryCell Cell(string id, string kind, string item, string? recipe, Dictionary<string, string> entities) =>
                new(id, 1, new(0, 0, true), kind, item, recipe, entities, "ready", 100, Plan: new Dictionary<string, PlannedEntity>());
            var producer = Cell("producer", "assembler", "assembling-machine-1", "automation-science-pack",
                new() { ["machine"] = "machine", ["input-chest"] = "input" });
            var lab = Cell("laboratory", "lab", "lab", null, new() { ["machine"] = "lab" });
            await new FactoryRegistry(directory).SaveAsync(new(1, game.Scope.WorldId, [new(1, new(0, 0), 4, 6, 6)], [producer, lab]), default);
            var journal = new Journal();

            var result = await new FactoryResearchController(game, journal, directory).RunAsync("target");

            Assert.Equal(1, game.Selections);
            Assert.Equal(0, game.PhotosAtSelection);
            Assert.Equal(0, game.Transfers);
            Assert.Equal(0, result.Rounds);
            Assert.Empty(result.Procured);
            Assert.Contains("research-selection-replaced", journal.Types);
            Assert.DoesNotContain("factory-logistics", journal.Types);
            Assert.Equal(photosBeforeCompletion == 1 ? 0 : 1, journal.Types.Count(t => t == "factory-automation-plan"));
            Assert.Equal("factory-research-result", journal.Types[^1]);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Journal : IControllerJournal
    {
        public IReadOnlyDictionary<string, long> Shortfall { get; private set; } = new Dictionary<string, long>();
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Types.Add(type);
            if (data is LogisticsResult result) Shortfall = result.Shortfall;
            return Task.CompletedTask;
        }
    }

    /// <summary>A final science transfer finishes research while an unprocurable synthetic ingredient is still short.</summary>
    private sealed class FinishingGame(int photosBeforeCompletion = 0, string initialSelection = "target") : IGameClient
    {
        private readonly ProductionCatalog catalog = Catalog();
        private readonly SpatialSnapshot map = FactoryMaps.Grass(8,
            [new("lab", "lab", new(2.5, .5), new(new(1.3, -.7), new(3.7, 1.7)), 0, "own")]);
        private long tick = 100;
        private bool researched;
        private string selected = initialSelection;
        private int factoryPhotos;
        private OperationSubmission? selection;
        private long selectionTick;
        public ActorScope Scope => catalog.Scope;
        public int Transfers { get; private set; }
        public int ResearchReads { get; private set; }
        public int Selections { get; private set; }
        public int PhotosAtSelection { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            if (researched && request.Action is "observe" or "submit")
                throw new InvalidOperationException("Research is already complete; no procurement or extra native work is permitted.");
            tick++;
            object data;
            switch (request.Action)
            {
                case "production_catalog": data = catalog with { CollectedTick = tick }; break;
                case "spatial": data = map with { CollectedTick = tick }; break;
                case "observe":
                    data = new { scope = Scope, collectedTick = tick,
                        coverage = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>() };
                    break;
                case "technologies":
                    data = new { items = new[] { new NativeTechnology("target", true, false, true, [], [new("automation-science-pack", 1)], 1, 60) },
                        total = 1, offset = 0, limit = request.Arguments.TryGetProperty("name", out _) ? 1 : 100, collectedTick = tick, complete = true };
                    break;
                case "power_state": data = new PowerState(Scope, tick, 1, 1, true, [], []); break;
                case "research_state":
                    ResearchReads++;
                    if (photosBeforeCompletion > 0 && factoryPhotos >= photosBeforeCompletion && selected == "target") researched = true;
                    data = new ResearchSnapshot(Scope, tick, 1, "target", researched, researched ? 1 : 0,
                        new Dictionary<string, LaboratoryPrototype>(), [], new Dictionary<string, double>(), true, true,
                        new Dictionary<string, long>(), new Dictionary<string, double>(), researched ? null : selected);
                    break;
                case "factory_snapshot":
                    factoryPhotos++;
                    FactoryRecord[] records =
                    [
                        Record("actor", "entity", "actor", new { role = "actor", type = "character", position = map.Actor.Position, mainInventoryId = "bag" }),
                        Record("bag", "inventory", "actor", new { items = new Dictionary<string, long> { ["automation-science-pack"] = researched ? 0 : 1 } }),
                        Record("power", "entity", "power", new { role = "factory", type = "electric-energy-interface", position = new MapPosition(0, 0) }),
                        Record("machine", "entity", "machine", new { role = "factory", type = "assembling-machine", position = new MapPosition(0, 0) }),
                        Record("input", "entity", "input", new { role = "factory", type = "container", position = new MapPosition(0, 0) }),
                        Record("lab", "entity", "lab", new { role = "factory", type = "lab", position = new MapPosition(2.5, .5) })
                    ];
                    data = new FactorySnapshotPage("stock", Scope, Scope, tick, tick + 1000, records.Length, 0, records.Length, true,
                        Protocol.ToElement(new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }), records);
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    if (submission.Kind == "research")
                    {
                        Assert.Equal("target", submission.Args.GetProperty("technology").GetString());
                        Assert.True(submission.Args.GetProperty("replace").GetBoolean());
                        Selections++;
                        PhotosAtSelection = factoryPhotos;
                        selected = "target";
                        selection = submission;
                        selectionTick = tick;
                        data = new { submission.OperationId, submission.Kind, status = "completed", acceptedTick = tick, updatedTick = tick,
                            effects = new { technology = "target" } };
                        break;
                    }
                    Assert.Equal("insert", submission.Kind);
                    Assert.Equal("lab", submission.Args.GetProperty("entityId").GetString());
                    Assert.Equal("automation-science-pack", submission.Args.GetProperty("item").GetString());
                    Assert.Equal(1, submission.Args.GetProperty("count").GetInt32());
                    Transfers++;
                    researched = true;
                    data = new { submission.OperationId, submission.Kind, status = "completed", acceptedTick = tick, updatedTick = tick,
                        effects = new { requested = 1, transferred = 1 } };
                    break;
                case "operation" when selection is not null:
                    Assert.Equal(selection.OperationId, request.Arguments.GetProperty("operationId").GetString());
                    data = new { selection.OperationId, selection.Kind, status = "completed", acceptedTick = selectionTick, updatedTick = selectionTick,
                        effects = new { technology = "target" } };
                    break;
                default: throw new InvalidOperationException($"Unexpected work after the research finished: {request.Action}");
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }

        private static FactoryRecord Record(string id, string kind, string entity, object data) => new(id, kind, entity, id, Protocol.ToElement(data));
        private static ProductionCatalog Catalog()
        {
            var early = Catalogs.Raw();
            NativeRecipe Equipment(string item) => new(item, true, "crafting", 1, [], [new(item, "item", 1)], false);
            return early with
            {
                Recipes = [Equipment("assembling-machine-1"), Equipment("inserter"), Equipment("small-electric-pole"), Equipment("lab"),
                    new("automation-science-pack", true, "crafting", 5, [new("prepared-substrate", "item", 1)], [new("automation-science-pack", "item", 1)], false)],
                Items = new Dictionary<string, NativeItem>(early.Items) { ["lab"] = new(0, 10), ["prepared-substrate"] = new(0, 100) },
                Mining = new Dictionary<string, NativeMaterial[]>(), MiningSourceTypes = new Dictionary<string, string>(),
                Machines = new Dictionary<string, NativeFurnace>()
            };
        }
    }
}
