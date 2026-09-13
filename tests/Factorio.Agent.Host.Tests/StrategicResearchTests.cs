using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicResearchTests
{
    [Fact]
    public async Task DefenseProposalCompletesFromInstalledReservesWithoutCarriedStockExecution()
    {
        var game = new Game { IncludeDefense = true, DefenseRounds = 100 };
        var result = await new StrategicProductionController(game, new Planner(defense: true), new Journal()).RunOnceAsync();
        Assert.Null(result.Production);
        Assert.Null(result.UnsupportedReason);
        Assert.Equal(1, result.Defense!.ReadyCount);
        Assert.Equal(0, result.Defense.Built);
        Assert.DoesNotContain("submit", game.Actions);
    }

    [Fact]
    public async Task StrategyReceivesInstalledDefenseReadinessWithoutCoordinates()
    {
        var planner = new Planner();
        await new StrategicProductionController(new Game { IncludeDefense = true }, planner, new Journal()).RunOnceAsync();
        using var facts = System.Text.Json.JsonDocument.Parse(planner.Context!.Facts);
        Assert.True(facts.RootElement.TryGetProperty("knownDefenses", out var defense));
        Assert.Equal(1, defense.GetProperty("surfaceIndex").GetInt32());
        var turret = Assert.Single(defense.GetProperty("turrets").EnumerateArray());
        Assert.Equal(1, turret.GetProperty("installed").GetInt32());
        Assert.Equal(0, turret.GetProperty("readyWithReserve").GetInt32());
        Assert.Equal(94, turret.GetProperty("rounds").GetInt64());
        Assert.DoesNotContain("position", planner.Context.Facts);
    }

    [Fact]
    public async Task ResearchProposalUsesScientificExecutionAndFreshCompletionEvidence()
    {
        var game = new Game();
        var planner = new Planner();
        var result = await new StrategicProductionController(game, planner, new Journal()).RunOnceAsync();
        Assert.Null(result.Production);
        Assert.True(result.Research!.Researched);
        Assert.Equal("automation", result.Research.Target);
        Assert.DoesNotContain("submit", game.Actions);
        Assert.Contains("nativeTechnologyIdentifiers", planner.Context!.Facts);
        Assert.Contains("physicalStocks", planner.Context.Facts);
        Assert.DoesNotContain("position", planner.Context.Facts);
    }
    [Fact]
    public async Task UnsupportedProposalReturnsExplicitFeedbackWithoutGoalExecution()
    {
        var game = new Game();
        var result = await new StrategicProductionController(game, new Planner(true), new Journal()).RunOnceAsync();
        Assert.NotNull(result.UnsupportedReason);
        Assert.Null(result.Production);
        Assert.Null(result.Research);
        Assert.DoesNotContain("submit", game.Actions);
    }
    private sealed class Planner(bool unsupported = false, bool defense = false) : IStrategicPlanner
    {
        public StrategicContext? Context { get; private set; }
        public Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            if (defense) return Task.FromResult(new GoalProposal(context.ObservationId, "Protect the factory", GoalCategory.Defense,
                "gun-turret", 1, GoalUnit.Items, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)));
            return Task.FromResult(new GoalProposal(context.ObservationId, "Complete automation", unsupported ? GoalCategory.Other : GoalCategory.Research,
                "automation", 1, GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)));
        }
    }
    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Game : IGameClient
    {
        public bool IncludeDefense { get; init; }
        public int DefenseRounds { get; init; } = 94;
        public List<string> Actions { get; } = [];
        private long tick;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            tick++;
            var scope = new ActorScope("world", "session", "actor", 1, 1);
            FactoryRecord[] records = IncludeDefense ?
            [
                new("actor", "entity", "actor", "character", Protocol.ToElement(new { role = "actor", surfaceIndex = 1, position = new MapPosition(0, 3) })),
                new("t", "entity", "t", "gun-turret", Protocol.ToElement(new { role = "factory", surfaceIndex = 1, type = "ammo-turret",
                    position = new MapPosition(0, 0), quality = "normal", active = true, ammoInventoryId = "ammo:t", ammoRounds = DefenseRounds, defenseReady = true, defenseRange = 18 })),
                new("ammo:t", "inventory", "t", "turret-ammo", Protocol.ToElement(new { items = new Dictionary<string, long> { ["firearm-magazine"] = 10 } }))
            ] : [];
            object data = request.Action switch
            {
                "observe" => new
                {
                    scope,
                    snapshotId = tick,
                    agent = new { alive = true, health = 250, inventory = new Dictionary<string, long>(), ammoRounds = 100 },
                    environment = new { },
                    enemies = Array.Empty<object>(),
                    resources = Array.Empty<object>(),
                    entities = Array.Empty<object>()
                },
                "factory_snapshot" => new FactorySnapshotPage("factory", scope, scope, tick, tick + 1000, records.Length, 0, records.Length, true,
                    Protocol.ToElement(new { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }), records),
                "production_catalog" => new ProductionCatalog(scope, tick, [], new Dictionary<string, NativeItem>
                    { ["firearm-magazine"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10),
                      ["gun-turret"] = new(0, 50, PlaceEntity: "gun-turret", PlaceEntityType: "ammo-turret") }, new Dictionary<string, NativeMaterial[]>(),
                    new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>(),
                    Turrets: IncludeDefense ? new Dictionary<string, NativeTurret> { ["gun-turret"] = new("gun-turret", 18, ["bullet"]) } : null),
                "technologies" => new
                {
                    items = new[] { new NativeTechnology("automation", true, true, false, [], [new("red", 1)], 10, 600) },
                    total = 1,
                    offset = 0,
                    limit = request.Arguments.TryGetProperty("name", out _) ? 1 : 100,
                    collectedTick = tick,
                    complete = true
                },
                _ => throw new InvalidOperationException("Unexpected execution outside research completion reading.")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
