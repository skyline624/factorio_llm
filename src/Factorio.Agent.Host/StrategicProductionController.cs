using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record StrategicGoalResult(GoalProposal Goal, StockGoalResult? Production = null, ResearchGoalResult? Research = null);

/// <summary>Grounds semantic production or research goals into verified native execution.</summary>
public sealed class StrategicProductionController(IGameClient game, IStrategicPlanner planner, IControllerJournal journal)
{
    public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default)
    {
        GameResponse observation = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200 }), token);
        if (!observation.Ok) throw new GameRpcException(observation.Error!);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        TechnologyObservation science = await new TechnologyClient(game).ReadAllAsync(token);
        ActorScope scope = observation.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (catalog.Scope != scope || science.Scope != scope) throw new InvalidDataException("Strategic observations span different actor scopes.");
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
            availableSolidRecipes = catalog.Recipes.Where(r => r.Enabled && r.Products.All(p => p.DeterministicItem) && r.Ingredients.All(p => p.DeterministicItem))
                .Select(r => r.Name).ToArray(),
            technologyCollection = new { science.StartTick, science.EndTick },
            nativeTechnologyIdentifiers = science.Technologies.Keys.Order(StringComparer.Ordinal).ToArray(),
            researchedTechnologies = science.Technologies.Values.Where(t => t.Researched).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray(),
            availableResearch = science.Technologies.Values.Where(t => t.Enabled && t.Available && !t.Researched)
                .Select(t => new { t.Name, t.Count, t.Ingredients, t.Trigger }).ToArray(),
            executionCapabilities = "Production goals use category production, unit items and an exact native item identifier, up to 1000 carried items. " +
                "C# explores, mines, hand-crafts, installs or reuses burner production, powered assemblers and local steam supply. " +
                "Research goals use category research, unit completion, quantity 1 and an exact native technology identifier. C# resolves native prerequisites, supported craft-item triggers and laboratory research, including science production and power maintenance. " +
                "Only deterministic solid production and bounded science batches are executable so far; fluid networks and industrial transport remain unsupported. " +
                "Choose an unmet useful goal toward the rocket. Other meaningful goals remain permissible proposals with explicit unsupported results.",
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
        string? reason = GroundingFailure(goal, observationId, catalog, science.Technologies);
        if (reason is not null)
        {
            await journal.AppendAsync("grounding-unsupported", new { goal, reason }, token);
            throw new InvalidOperationException(reason);
        }
        // Production recollects inventory, recipes and geometry before acting; the LLM context is never a precondition.
        if (goal.Category == GoalCategory.Research)
            return new(goal, Research: await new ResearchGoalExecutor(game, journal).RunAsync(goal.Target, token));
        return new(goal, Production: await new ProductionGoalExecutor(game, journal).RunAsync(goal.Target, (int)goal.Quantity, token));
    }

    public static string? GroundingFailure(GoalProposal goal, string observationId, ProductionCatalog catalog,
        IReadOnlyDictionary<string, NativeTechnology>? technologies = null)
    {
        if (goal.ObservationId != observationId) return "The proposal references a different observation.";
        if (goal.Category == GoalCategory.Research)
        {
            if (goal.Unit != GoalUnit.Completion || goal.Quantity != 1) return "Research requires a completion goal with quantity 1.";
            if (technologies is null || !technologies.ContainsKey(goal.Target)) return "The target is not an exact observed native technology identifier.";
            return null;
        }
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
