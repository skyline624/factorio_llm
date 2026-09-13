using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record StrategicGoalResult(GoalProposal Goal, StockGoalResult? Production = null, ResearchGoalResult? Research = null, string? UnsupportedReason = null, FluidProductionResult? Fluid = null, RocketLaunchResult? Rocket = null);

/// <summary>Grounds semantic production or research goals into verified native execution.</summary>
public sealed class StrategicProductionController(IGameClient game, IStrategicPlanner planner, IControllerJournal journal) : IStrategicGoalRunner
{
    public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
    {
        GameResponse observation = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200 }), token);
        if (!observation.Ok) throw new GameRpcException(observation.Error!);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        TechnologyObservation science = await new TechnologyClient(game).ReadAllAsync(token);
        FactorySnapshot factory = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        ActorScope scope = observation.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (catalog.Scope != scope || science.Scope != scope || factory.Scope != scope) throw new InvalidDataException("Strategic observations span different actor scopes.");
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
            knownFactory = new { factory.CollectedTick, factory.Coverage, physicalStocks = factory.SummarizeStocks(),
                interpretation = "Inventory totals INCLUDE the actor and known corpses; do not add them to agent.inventory. Physical stock is not a promise of immediate availability. Transit and fluids are separate." },
            nativeFluidIdentifiers = catalog.Recipes.SelectMany(r => r.Ingredients.Concat(r.Products)).Where(m => m.Type == "fluid")
                .Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            availableFluidConversions = catalog.Recipes.Where(r => r.Enabled && r.Ingredients.Count == 1 && r.Products.Count == 1
                && r.Ingredients[0].DeterministicFluid && r.Products[0].DeterministicFluid).Select(r => r.Name).ToArray(),
            technologyCollection = new { science.StartTick, science.EndTick },
            nativeTechnologyIdentifiers = science.Technologies.Keys.Order(StringComparer.Ordinal).ToArray(),
            researchedTechnologies = science.Technologies.Values.Where(t => t.Researched).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray(),
            availableResearch = science.Technologies.Values.Where(t => t.Enabled && t.Available && !t.Researched)
                .Select(t => new { t.Name, t.Count, t.Ingredients, t.Trigger }).ToArray(),
            executionCapabilities = "Production goals use category production, unit items and an exact native item identifier, up to 1000 carried items. " +
                "C# explores, mines, hand-crafts, installs or reuses furnaces, powered assemblers and native steam supply. " +
                "Prefer machine production and fuel over bulk hand mining. C# can prepare or reuse burner or electric drills feeding compatible storage or furnaces for deterministic solid deposits such as coal, stone and iron ore. Electric solid extraction can extend a known power network with calculated poles and service a distant connected boiler. Trees still require manual harvesting and machine bootstrap may need small manual quantities. " +
                "Research goals use category research, unit completion, quantity 1 and an exact native technology identifier. C# resolves native prerequisites, supported craft-item triggers and laboratory research, including science production and power maintenance. It can also satisfy fluid resource mining triggers using a compatible electric extractor on an observed deposit near an existing network, with at most one new pole. Remote fluid-extraction outposts and solid-resource mining triggers remain unsupported. " +
                "Fluid production goals use category production, unit fluid_units and an exact native fluid identifier, up to 100000 units in the known factory. C# supports native refinery configuration and ordinary pipe routes in the observed construction area. Compatible chemical recipes may combine deterministic solid and fluid inputs, including sulfuric acid output, with finite fluid preparation and native pipe connections. Observed solid producer outputs can supply assemblers through calculated belts and inserters. Long-distance fluid networks, temperature-constrained chemistry, complete factory logistics and automatic relocation after resource depletion remain incomplete. " +
                "Launch goals use category launch, unit completion, quantity 1 and an exact native rocket-silo item identifier. Research the silo and rocket-part recipes first. C# reuses or installs a silo, supplies bounded batches from native requirements and verifies the engine launch counter. Local powered placement and existing production capabilities still bound execution. " +
                "Choose an unmet useful goal toward the rocket. Other meaningful goals remain permissible proposals with explicit unsupported results.",
            nativeSiloItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "rocket-silo").Select(p => p.Key).ToArray(),
            scope = "Local observed resources; known own buildings; exact actor inventory at observedTick. Hidden areas and enemies are unknown."
        }, Protocol.Json);
        var context = new StrategicContext(observationId, facts,
            "Progress toward a normal hostile base-game rocket launch using verified stock and native actions. Minimize manual mining by reusing and building production machines.", previousResult);
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
            return new(goal, UnsupportedReason: reason);
        }
        // Production recollects inventory, recipes and geometry before acting; the LLM context is never a precondition.
        if (goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.FluidUnits)
            return new(goal, Fluid: await new FluidProductionController(game, journal).RunAsync(goal.Target, (double)goal.Quantity, token));
        if (goal.Category == GoalCategory.Research)
            return new(goal, Research: await new ResearchGoalExecutor(game, journal).RunAsync(goal.Target, token));
        if (goal.Category == GoalCategory.Launch)
            return new(goal, Rocket: await new RocketLaunchController(game, journal).RunAsync(goal.Target, token));
        return new(goal, Production: await new ProductionGoalExecutor(game, journal).RunAsync(goal.Target, (int)goal.Quantity, token));
    }

    public static string? GroundingFailure(GoalProposal goal, string observationId, ProductionCatalog catalog,
        IReadOnlyDictionary<string, NativeTechnology>? technologies = null)
    {
        if (goal.ObservationId != observationId) return "The proposal references a different observation.";
        if (goal.Category == GoalCategory.Launch)
        {
            if (goal.Unit != GoalUnit.Completion || goal.Quantity != 1) return "Launch requires a completion goal with quantity 1.";
            if (!catalog.Items.TryGetValue(goal.Target, out var silo) || silo.PlaceEntityType != "rocket-silo")
                return "The target is not an exact native rocket-silo item identifier.";
            return null;
        }
        if (goal.Category == GoalCategory.Research)
        {
            if (goal.Unit != GoalUnit.Completion || goal.Quantity != 1) return "Research requires a completion goal with quantity 1.";
            if (technologies is null || !technologies.ContainsKey(goal.Target)) return "The target is not an exact observed native technology identifier.";
            return null;
        }
        if (goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.FluidUnits)
        {
            if (goal.Quantity is <= 0 or > 100000) return "Fluid stock requires a quantity greater than zero and at most 100000.";
            if (!catalog.Recipes.SelectMany(r => r.Ingredients.Concat(r.Products)).Any(m => m.Type == "fluid" && m.Name == goal.Target))
                return "The target is not an exact observed native fluid identifier.";
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
