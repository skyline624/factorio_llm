using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class GrowingOutputLogisticsTests
{
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
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>Six items in the cycle photograph; a different native quantity after approach, with measured receipts.</summary>
    private sealed class OutputGame(int carried, int currentStock, bool lostReply, bool labScience = false) : IGameClient
    {
        private readonly ProductionCatalog catalog = Catalogs.Raw();
        private readonly SpatialSnapshot map = FactoryMaps.Grass(8, labScience
            ? new[] { Lab("lab-0", 3.5, -.5), Lab("lab-1", -.5, 3.5), Lab("lab-2", 4.5, 4.5) }
            : [new("chest", "iron-chest", new(2.5, .5), new(new(2.15, .15), new(2.85, .85)), 0, "own")]);
        private object? receipt;
        private string? operationId;
        private string Item => labScience ? "automation-science-pack" : "iron-plate";
        public Dictionary<string, long> LabStocks { get; } = new() { ["lab-0"] = 10, ["lab-1"] = 0, ["lab-2"] = 0 };
        public ActorScope Scope => catalog.Scope;
        public int Carried { get; private set; } = carried;
        public int Submissions { get; private set; }
        public int Queries { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            object data;
            switch (request.Action)
            {
                case "production_catalog": data = catalog with { CollectedTick = 100 }; break;
                case "spatial": data = map; break;
                case "observe":
                    data = new
                    {
                        scope = Scope, collectedTick = 100,
                        coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } }, enemies = Array.Empty<object>()
                    };
                    break;
                case "factory_snapshot":
                    var records = new List<FactoryRecord>
                    {
                        Record("actor", "entity", "actor", new { role = "actor", type = "character", position = map.Actor.Position, mainInventoryId = "bag" }),
                        Record("bag", "inventory", "actor", new { items = new Dictionary<string, long> { [Item] = Carried } })
                    };
                    if (labScience)
                        foreach (var lab in map.Entities)
                        {
                            records.Add(Record(lab.Id, "entity", lab.Id, new { role = "factory", type = "lab", position = lab.Position }));
                            records.Add(Record(lab.Id + "-stock", "inventory", lab.Id, new { items = new Dictionary<string, long> { [Item] = LabStocks[lab.Id] } }));
                        }
                    else
                    {
                        records.Add(Record("chest", "entity", "chest", new { role = "factory", type = "container", position = new MapPosition(2.5, .5) }));
                        records.Add(Record("stock", "inventory", "chest", new { items = new Dictionary<string, long> { [Item] = 6 } }));
                    }
                    data = new { snapshotId = "stock", scope = Scope, snapshotScope = Scope, collectedTick = 100, expiresTick = 1000,
                        totalRecords = records.Count, offset = 0, nextOffset = records.Count, complete = true,
                        coverage = new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }, records };
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Assert.Equal(labScience ? "insert" : "take", submission.Kind);
                    string target = submission.Args.GetProperty("entityId").GetString()!;
                    if (labScience) Assert.Contains(target, LabStocks.Keys);
                    else Assert.Equal("chest", target);
                    Assert.Equal(Item, submission.Args.GetProperty("item").GetString());
                    Assert.Equal(Scope, submission.Scope);
                    Submissions++;
                    operationId = submission.OperationId;
                    int requested = submission.Args.GetProperty("count").GetInt32();
                    int moved = Math.Min(requested, labScience ? Carried : currentStock);
                    Carried += labScience ? -moved : moved;
                    if (labScience) LabStocks[target] += moved;
                    receipt = new { submission.OperationId, submission.Kind, status = moved == requested ? "completed" : "partial",
                        acceptedTick = 100, updatedTick = 100, effects = new { requested, transferred = moved } };
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
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }

        private static FactoryRecord Record(string id, string kind, string entity, object data) => new(id, kind, entity, id, Protocol.ToElement(data));
        private static SpatialEntity Lab(string id, double x, double y) => new(id, "lab", new(x, y), new(new(x - 1.2, y - 1.2), new(x + 1.2, y + 1.2)), 0, "own");
    }
}
