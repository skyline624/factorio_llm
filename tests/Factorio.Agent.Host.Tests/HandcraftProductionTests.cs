using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class HandcraftProductionTests
{
    [Theory]
    [InlineData(false, "submachine-gun", 1)]
    [InlineData(true, "iron-gear-wheel", 10)]
    public async Task KitCraftingUsesCollectedIngredientsAndPreservesTheTasksIntermediateStock(
        bool protectGears, string recipe, int count)
    {
        var game = new Game(plates: 50, gears: 20);
        var journal = new Journal();
        var protectedStock = new Dictionary<string, long> { ["iron-plate"] = 20, ["iron-gear-wheel"] = protectGears ? 20 : 0 };
        await Assert.ThrowsAsync<PlanObservedException>(() => new ProductionController(game, journal)
            .HandcraftFromCarriedAsync("submachine-gun", 1, protectedStock: protectedStock));
        Assert.Equal(("craft", recipe, count), (journal.Plan!.Kind, journal.Plan.Recipe!.Name, journal.Plan.Quantity));
        // Even a configured gear assembler and replenished stock do not turn this kit into a transport job.
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task MissingCarriedIngredientsCannotBeReplacedWithFactoryStockOrMining()
    {
        var game = new Game(plates: 0, gears: 0);
        var journal = new Journal { StopAtPlan = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProductionController(game, journal)
            .HandcraftFromCarriedAsync("submachine-gun", 1));
        Assert.Equal("unsupported", journal.Plan!.Kind);
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task AChangedActorCannotCraftThePreviousIncarnationsCollectedEquipment()
    {
        var game = new Game(plates: 50, gears: 20);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProductionController(game, new Journal())
            .HandcraftFromCarriedAsync("submachine-gun", 1, expectedScope: game.Scope with { Incarnation = 2 }));
        Assert.Equal(["observe"], game.Calls);
    }

    private sealed class PlanObservedException : Exception;

    private sealed class Journal : IControllerJournal
    {
        public bool StopAtPlan { get; init; } = true;
        public ProductionStep? Plan { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "production-step")
            {
                Plan = Protocol.ToElement(data).GetProperty("step").Deserialize<ProductionStep>(Protocol.Json);
                if (StopAtPlan) throw new PlanObservedException();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Game(long plates, long gears) : IGameClient
    {
        private readonly SpatialSnapshot map = FactoryMaps.Grass(12);
        public ActorScope Scope => map.Scope;
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "production_catalog" => Catalogs.Early() with
                {
                    Scope = map.Scope, CollectedTick = map.CollectedTick,
                    Recipes = [new("iron-gear-wheel", true, "crafting", .5, [new("iron-plate", "item", 2)],
                        [new("iron-gear-wheel", "item", 1)], false),
                        new("submachine-gun", true, "crafting", 10, [new("iron-gear-wheel", "item", 10), new("iron-plate", "item", 10)],
                            [new("submachine-gun", "item", 1)], false)],
                    Assemblers = new Dictionary<string, NativeAssembler>
                        { ["assembling-machine-1"] = new("assembling-machine-1", new Dictionary<string, bool> { ["crafting"] = true }, .5, 1000, 255) }
                },
                "spatial" => map,
                "observe" => new
                {
                    scope = map.Scope, collectedTick = map.CollectedTick, coverage = new { knownInventoriesComplete = true },
                    agent = new { alive = true, controlMode = "ai", inventory = new Dictionary<string, long>
                        { ["iron-plate"] = plates, ["iron-gear-wheel"] = gears } },
                    entities = new[]
                    {
                        new { id = "configured-gear-assembler", name = "assembling-machine-1", position = new MapPosition(2, 0),
                            recipe = (string?)"iron-gear-wheel", inventories = new { output = new { items = new Dictionary<string, long>() } } },
                        new { id = "replenished-stock", name = "iron-chest", position = new MapPosition(3, 0),
                            recipe = (string?)null, inventories = new { output = new { items = new Dictionary<string, long> { ["iron-plate"] = 100 } } } }
                    }
                },
                _ => throw new InvalidOperationException($"Unexpected query or mutation: {request.Action}")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, map.CollectedTick, Protocol.ToElement(data)));
        }
    }
}
