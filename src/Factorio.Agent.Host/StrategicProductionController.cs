using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

/// <summary>One real strategic proposal grounded into the currently supported production capability.</summary>
public sealed class StrategicProductionController(IGameClient game, IStrategicPlanner planner, IControllerJournal journal)
{
    public async Task<StockGoalResult> RunOnceAsync(CancellationToken token = default)
    {
        GameResponse observation = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200 }), token);
        if (!observation.Ok) throw new GameRpcException(observation.Error!);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        JsonElement agent = observation.Data.GetProperty("agent");
        string observationId = $"{observation.Data.GetProperty("snapshotId").GetInt64()}:{observation.Tick}";
        // Whitelist factual fields: no session credentials, player names or coordinates leave the machine.
        string facts = JsonSerializer.Serialize(new
        {
            observedTick = observation.Tick,
            agent = new
            {
                alive = agent.GetProperty("alive"),
                health = agent.GetProperty("health"),
                inventory = agent.GetProperty("inventory"),
                ammoRounds = agent.GetProperty("ammoRounds")
            },
            environment = observation.Data.GetProperty("environment"),
            visibleEnemyCount = observation.Data.GetProperty("enemies").ValueKind == JsonValueKind.Array
                ? observation.Data.GetProperty("enemies").GetArrayLength() : 0,
            knownResources = Names(observation.Data.GetProperty("resources")),
            knownBuildings = Names(observation.Data.GetProperty("entities")),
            availableSolidRecipes = catalog.Recipes.Where(r => r.Enabled && r.Products.All(p => p.DeterministicItem))
                .Select(r => r.Name).ToArray(),
            executionCapabilities = "Current grounder supports production goals for a solid item stock carried by the actor, up to 1000 items per goal. " +
                "It can explore, mine, hand-craft and feed a burner furnace. C# can also install or reuse a burner drill feeding an existing compatible furnace when native geometry and resources permit. " +
                "C# selects the production method from fresh observations; propose a needed stock above the current inventory to make progress. " +
                "Other meaningful goals may still be proposed and will return an explicit unsupported result. " +
                "Research execution, automatic machine prerequisite construction and fluid networks are not implemented yet.",
            scope = "Local observed resources; known own buildings; exact actor inventory at observedTick. Hidden areas and enemies are unknown."
        }, Protocol.Json);
        var context = new StrategicContext(observationId, facts,
            "Progress toward a normal hostile base-game rocket launch using verified stock and native actions.");
        await journal.AppendAsync("strategic-context", context, token);
        var defense = new DefenseController(game, journal);
        GoalProposal goal;
        using var inferenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<GoalProposal> pending = planner.ProposeAsync(context, inferenceCancellation.Token);
        try
        {
            while (!pending.IsCompleted)
            {
                await defense.StepAsync(token);
                await Task.WhenAny(pending, Task.Delay(150, token));
                token.ThrowIfCancellationRequested();
            }
            goal = await pending;
        }
        finally
        {
            inferenceCancellation.Cancel();
            try { await pending; } catch (Exception) when (pending.IsCanceled || pending.IsFaulted) { }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await defense.StopOwnedActionAsync(stop.Token);
        }
        await journal.AppendAsync("strategic-goal", goal, token);
        string? reason = GroundingFailure(goal, observationId, catalog);
        if (reason is not null)
        {
            await journal.AppendAsync("grounding-unsupported", new { goal, reason }, token);
            throw new InvalidOperationException(reason);
        }
        // Production recollects inventory, recipes and geometry before acting; the LLM context is never a precondition.
        return await new ProductionGoalExecutor(game, journal).RunAsync(goal.Target, (int)goal.Quantity, token);
    }

    public static string? GroundingFailure(GoalProposal goal, string observationId, ProductionCatalog catalog)
    {
        if (goal.ObservationId != observationId) return "The proposal references a different observation.";
        if (goal.Category != GoalCategory.Production || goal.Unit != GoalUnit.Items)
            return "This goal is not yet supported by the solid-stock production grounder.";
        if (goal.Quantity is < 1 or > 1000 || decimal.Truncate(goal.Quantity) != goal.Quantity)
            return "The current production batch requires 1 to 1000 whole items.";
        if (!catalog.Items.ContainsKey(goal.Target)) return "The target is not an exact native item identifier; no guessed alias is executed.";
        return null;
    }

    private static string[] Names(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).Distinct(StringComparer.Ordinal).Order().ToArray()
        : [];
}
