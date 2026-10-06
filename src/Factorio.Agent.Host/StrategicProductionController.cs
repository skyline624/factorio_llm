using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record StrategicGoalResult(GoalProposal Goal, StockGoalResult? Production = null, ResearchGoalResult? Research = null, string? UnsupportedReason = null, FluidProductionResult? Fluid = null, RocketLaunchResult? Rocket = null, DefenseDeploymentResult? Defense = null,
    AutomationPlan? Automation = null, LogisticsResult? Logistics = null, PerimeterDefenseResult? Perimeter = null,
    ResourceDiscoveryResult? Discovery = null, ResourceSearchProgressResult? SearchProgress = null);

/// <summary>Grounds semantic production or research goals into verified native execution.</summary>
public sealed class StrategicProductionController(IGameClient game, IStrategicPlanner planner, IControllerJournal journal,
    string? factoryDirectory = null, DecisionModelClient? shadow = null) : IStrategicGoalRunner
{
    private const string CompactExecutionCapabilities = """
        Propose one unmet goal toward a hostile base-game rocket, using exact native identifiers and positive quantities.
        Production/items: at most 1000 carried items, from observed stock or native crafting, furnaces and powered assemblers.
        Production/items_per_minute: persistent automation, at most 600; C# builds resource, furnace, assembler and supported chemical chains with calculated placements, belts, pipes and steam power. Coal, stone and ore plates use resource cells. Reuse and fuel machines; manual mining is limited to bootstrap and bounded repairs. Trees still need harvesting. Installed capacity is not measured throughput; stocks do not satisfy a rate target.
        Production/fluid_units: at most 100000 units in the known factory; compatible refinery and single-product chemical recipes. Supported solid automation includes plastic, sulfur, batteries, processing units, rocket fuel and electric engines, with researched recipes and equipment. Shared dependency plans account for native co-products. Refineries use finite isolated tanks: regulated cracking, sustained production after tank saturation, long-distance fluid networks and temperature-constrained chemistry remain incomplete. The actor services unconnected solid inputs and fuel; complete logistics and automatic relocation after depletion remain incomplete.
        Research/completion: quantity 1, exact technology; C# resolves prerequisites, craft-item and fluid-mining triggers, builds science cells/labs when available, supplies and observes native completion. Solid-resource mining triggers remain unsupported. Fluid extraction requires observed deposits and a reachable power network within the 128-link grid budget.
        Exploration/completion: quantity 1, exact resource; C# uses the force chart, dated hints and bounded local exploration with normal visibility and recent-death avoidance. Completion requires a fresh deposit observation; partial surveyed coverage is not discovery, global absence or safe extraction capacity.
        Launch/completion: quantity 1, native rocket-silo item, after native silo and rocket-part research. C# reuses, resumes or rebuilds its registered silo, supplies native requirements, maintains its factory and verifies the native launch counter. It stops stalled cells for reconciliation.
        Defense/items: 1 to 32 supported turrets, counting already installed active turrets with at least 100 rounds each. C# services existing turrets first; preventive coverage gaps alone are not an immediate attack. Defense/completion: quantity 1, supported wall item; C# calculates a finite defended ring and registers reconstruction and rearming. Neither goal promises continuous coverage.
        Construction, exploration, stock, native geometry and execution budgets still bound all goals. Unsupported proposals return explicit feedback. No coordinates, code, engine commands, invented stock or unverified success.
        """;

    public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
    {
        GameResponse observation = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200 }), token);
        if (!observation.Ok) throw new GameRpcException(observation.Error!);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        TechnologyObservation science = await new TechnologyClient(game).ReadAllAsync(token);
        FactorySnapshot factory = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        ActorScope scope = observation.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (catalog.Scope != scope || science.Scope != scope || factory.Scope != scope) throw new InvalidDataException("Strategic observations span different actor scopes.");
        var defenses = catalog.Turrets is { Count: > 0 } ? DefenseFactoryState.Read(factory, catalog) : null;
        bool automation = factoryDirectory is not null && FactoryDirector.Available(catalog);
        var automationReadiness = FactoryDirector.Readiness(catalog, science.Technologies);
        var factoryState = factoryDirectory is null ? null : await new FactoryRegistry(factoryDirectory).LoadAsync(scope.WorldId, token);
        var transportReadiness = FactoryTransportConversion.Readiness(factoryState, factory, catalog, science.Technologies);
        var cells = factoryState is null ? [] : factoryState.Cells.Where(c => c.Status == "ready").ToArray();
        JsonElement agent = observation.Data.GetProperty("agent");
        var defenseSituation = await AttackResponseFacts.ReadAsync(factoryDirectory, factoryState, factory, defenses, agent, token);
        string observationId = $"{observation.Data.GetProperty("snapshotId").GetInt64()}:{observation.Tick}";
        // Whitelist factual fields: no session credentials, player names or coordinates leave the machine.
        // The planner rejects contexts above 24,000 characters; a large factory drops the least decisive lists first.
        string facts = Facts(compact: false);
        if (facts.Length > 23_000) facts = Facts(compact: true);
        if (facts.Length > 23_000) facts = Facts(compact: true, omitResearchUnlocks: true);
        string Facts(bool compact, bool omitResearchUnlocks = false) => JsonSerializer.Serialize(new
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
            nativeResourceIdentifiers = catalog.Mining.Keys.Where(name => ResourceDiscoveryController.Supported(catalog, name))
                .Order(StringComparer.Ordinal).ToArray(),
            knownBuildings = Names(observation.Data.GetProperty("entities")).Take(compact ? 40 : int.MaxValue).ToArray(),
            availableSolidRecipes = catalog.Recipes.Where(r => r.Enabled && r.Products.All(p => p.DeterministicItem) && r.Ingredients.All(p => p.DeterministicItem))
                .Select(r => r.Name).ToArray(),
            knownFactory = new { factory.CollectedTick, factory.Coverage, physicalStocks = factory.SummarizeStocks(),
                interpretation = "Inventory totals INCLUDE the actor and known corpses; do not add them to agent.inventory. Physical stock is not a promise of immediate availability. Transit and fluids are separate." },
            knownDefenses = defenses is null ? null : new
            {
                factory.CollectedTick, defenses.SurfaceIndex,
                turrets = defenses.Turrets.GroupBy(t => t.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { name = g.Key, installed = g.Count(), active = g.Count(t => t.Active),
                        readyWithReserve = g.Count(DefenseDeploymentPlanner.Ready), rounds = g.Sum(t => t.Rounds) }).ToArray(),
                requiredReserveRounds = DefenseDeploymentPlanner.ReserveRounds,
                coveredIndustrialAnchors = defenses.Anchors.Count(a => DefenseDeploymentPlanner.Coverage(a, defenses.Turrets) > 0),
                exposedIndustrialAnchors = defenses.Anchors.Count(a => DefenseDeploymentPlanner.Coverage(a, defenses.Turrets) == 0),
                interpretation = "Known own industry on the current surface only; coverage uses native effective firing range of active loaded turrets. Turret ammunition is reserved, not available to ordinary production."
            },
            defenseSituation,
            nativeFluidIdentifiers = catalog.Recipes.SelectMany(r => r.Ingredients.Concat(r.Products)).Where(m => m.Type == "fluid")
                .Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            availableFluidConversions = catalog.Recipes.Where(r => r.Enabled && r.Ingredients.Count == 1 && r.Products.Count == 1
                && r.Ingredients[0].DeterministicFluid && r.Products[0].DeterministicFluid).Select(r => r.Name).ToArray(),
            technologyCollection = new { science.StartTick, science.EndTick },
            rocketResearchDependencies = SiloResearchDependencies.Read(catalog, science.Technologies),
            nativeTechnologyIdentifiers = compact ? null : science.Technologies.Keys.Order(StringComparer.Ordinal).ToArray(),
            researchedTechnologies = science.Technologies.Values.Where(t => t.Researched).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray(),
            researchUnlocksOmitted = omitResearchUnlocks,
            availableResearch = science.Technologies.Values.Where(t => t.Enabled && t.Available && !t.Researched)
                .Select(t => new { t.Name, t.Count, t.Ingredients, t.Trigger, unlocks = omitResearchUnlocks ? null : Unlocks(t.Effects) }).ToArray(),
            executionCapabilities = compact ? CompactExecutionCapabilities : "Production goals use category production, unit items and an exact native item identifier, up to 1000 carried items. " +
                "C# explores, mines, hand-crafts, installs or reuses furnaces, powered assemblers and native steam supply. " +
                "Prefer machine production and fuel over bulk hand mining. C# can prepare or reuse burner or electric drills feeding compatible storage or furnaces for deterministic solid deposits such as coal, stone and iron ore. Electric solid extraction can extend a known power network with calculated poles and service a distant connected boiler. Trees still require manual harvesting and machine bootstrap may need small manual quantities. " +
                "Research goals use category research, unit completion, quantity 1 and an exact native technology identifier. C# resolves native prerequisites, supported craft-item triggers and laboratory research, including science production and power maintenance. It can also satisfy fluid resource mining triggers by installing or reusing an owned compatible electric extractor on an observed deposit. C# can extend a known power network with calculated poles and service its connected steam supply. Exploration, reachable terrain and the 128-link grid budget still bound remote fluid extraction; solid-resource mining triggers remain unsupported. " +
                "Exploration goals use category exploration, unit completion, quantity 1 and an exact native resource identifier such as crude-oil. C# reads the force's chart, follows dated resource hints and uses bounded local exploration with ordinary visibility and recent-death avoidance. Completion proves a deposit in a fresh local observation, not extraction capacity or a safe factory site. A step budget can end with an incomplete search and measured new surveyed cells; that is not a discovered deposit or global absence. Continue a useful resource search from current observations when it added coverage, or choose a goal that supports safe exploration. Arbitrary area descriptions and item/fluid aliases are unsupported. " +
                "Fluid production goals use category production, unit fluid_units and an exact native fluid identifier, up to 100000 units in the known factory. C# supports native refinery configuration and ordinary pipe routes in the observed construction area. Compatible chemical recipes may combine deterministic solid and fluid inputs, including sulfuric acid output, with finite fluid preparation and native pipe connections. Observed solid producer outputs can supply assemblers through calculated belts and inserters. Long-distance fluid networks, temperature-constrained chemistry, complete factory logistics and automatic relocation after resource depletion remain incomplete. " +
                "Launch goals use category launch, unit completion, quantity 1 and an exact native rocket-silo item identifier. Research the silo and rocket-part recipes first. C# reuses or installs a silo, supplies bounded batches from native requirements and verifies the engine launch counter. Local powered placement and existing production capabilities still bound execution. " +
                "With the factory, rocket-part automation builds one silo cell fed by its chest and inserter plus enabled solid and chemical chains behind it, including plastic, processing units and rocket fuel when advanced oil processing and the consumer recipes are researched. Its refinery uses finite isolated co-product tanks; regulated cracking and sustained flow after those tanks fill remain incomplete. Launch goals prefer that cell, resume or rebuild it at its plan (never a second one) or build it when no powered silo stands, and keep logistics and maintenance running until the native rocket is ready; a cell that makes no progress stops the goal for reconciliation. " +
                "Defense goals use category defense, unit items, quantity 1 to 32 and a native supported turret item. Completion means at least that many active installed turrets on the actor's surface, each with at least 100 observed rounds: the quantity is a total that counts turrets already installed (knownDefenses), so adding turrets means requesting more than are installed. C# services existing turrets first, produces supplies, calculates placements near exposed known industry and reports measured coverage. This is a finite deployment and replenishment goal, not a guarantee of continuous perimeter coverage. " +
                "Perimeter goals use category defense, unit completion, quantity 1 and a native wall item such as stone-wall. C# rings the known factory core within one observed area with turret nests spaced by native range, outward wall shields and open gaps, loads turret reserves, and registers them so factory logistics rebuilds destroyed defenses and rearms turrets from carried magazines. " +
                "Automation goals use category production, unit items_per_minute, quantity up to 600 and an exact native item. Supported targets include deterministic mined solids such as coal and stone, ore plates supplied by resource cells, assembler products and burner-furnace products such as steel-plate. C# builds persistent resource cells on observed deposits and chest-fed assembler or furnace cells for the item and its intermediates, then restocks them. Explicit raw-rate targets require production capacity even when carried stock covers a temporary horizon. Prefer a persistent raw-rate target over repeated stock batches when the known raw capacity is inadequate. Research goals also build science cells and laboratories instead of hand-crafting packs when automation is available. Construction and exploration budgets can leave a requested rate partly supplied; installed capacity and native output must be observed. " +
                "Once the native research and equipment are available, automation includes plastic-bar, sulfur, batteries, processing units, rocket fuel and electric engines as well as solid consumers of those intermediates. All registered item targets share one dependency plan and add their demands, including common petroleum and acid stages. Simultaneous refinery outputs share the same native recipe cycles rather than duplicate crude and water demand. C# builds pumpjack extractors on an observed crude oil deposit, refineries, isolated finite co-product tanks, chemical plants and fluid-fed assemblers with calculated pipes; the actor transports solid inputs. Advanced oil processing and lubricant can serve item automation; direct fluid-stock goals still require their supported single-product recipes. Regulated cracking and sustained flow after tank saturation remain incomplete. Planned rates are bounded by cell and transport capacity and do not prove achieved native throughput. " +
                "Distant smelter rows can supply a depot beside the assembler bands through an observed belt route. Production and research goals request these supply lines; a damaged or unpowered line leaves its row available for direct collection. " +
                "Choose an unmet useful goal toward the rocket. Other meaningful goals remain permissible proposals with explicit unsupported results.",
            automatedFactory = new
            {
                available = automation,
                prerequisites = automationReadiness,
                transport = transportReadiness,
                assemblerCells = cells.Where(c => c.Kind == "assembler").GroupBy(c => c.Recipe!).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { recipe = g.Key, cells = g.Count() }).ToArray(),
                furnaceCells = cells.Where(c => c.Kind == FurnaceCellPlanner.Kind).GroupBy(c => c.Recipe!).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { recipe = g.Key, cells = g.Count() }).ToArray(),
                laboratories = cells.Count(c => c.Kind == "lab"),
                siloCells = cells.Count(c => c.Kind == SiloCellPlanner.Kind),
                rawSupply = factoryState is null ? [] : FactoryDirector.RawSupply(factoryState),
                enabledDrills = catalog.Items.Where(p => p.Value.PlaceEntityType == "mining-drill" && FactoryDirector.Enabled(catalog, p.Key))
                    .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(),
                rawInterpretation = "Resource cells mine ore patches for plates, coal and stone. Explicit items_per_minute goals can grow their persistent capacity; construction and exploration are bounded. The actor still carries products and fuel between unconnected cells, and temporary stock requests may use an early drill. Burner drills burn coal and mine at half an electric drill's speed; depleted cells no longer produce. Registered rates are calculated capacity, not measured sustained throughput.",
                fluidCells = cells.Where(c => c.Kind is FluidCellBuilder.MachineKind or FluidCellBuilder.ExtractorKind).GroupBy(c => c.Recipe ?? c.Kind)
                    .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { recipe = g.Key, cells = g.Count() }).ToArray(),
                supplyLines = cells.Where(c => c.Kind == SupplyLinePlanner.Kind).Select(c => new { row = c.Slot.Band, product = c.Recipe,
                    feeders = SupplyLines.FeederCells(c.Entities.Keys).Count }).ToArray(),
                perimeterTurrets = cells.Where(c => c.Kind == "turret").Sum(c => c.Entities.Count),
                perimeterWalls = cells.Where(c => c.Kind == "wall").Sum(c => c.Entities.Count),
                interpretation = "Persistent chest-fed cells keep producing while inputs last; the actor restocks them between goals."
            },
            nativeSiloItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "rocket-silo").Select(p => p.Key).ToArray(),
            nativeDefenseItems = catalog.Turrets?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [],
            nativeWallItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "wall").Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(),
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
        if (shadow is not null) await ShadowAsync(facts, previousResult, goal, token);
        string? reason = GroundingFailure(goal, observationId, catalog, science.Technologies, automation, factoryDirectory is not null,
            Obtained(factory, defenses, catalog));
        if (reason is not null)
        {
            await journal.AppendAsync("grounding-unsupported", new { goal, reason }, token);
            return new(goal, UnsupportedReason: reason);
        }
        // Actor-driven executors must not reuse or empty persistent factory cells.
        using var reserved = factoryDirectory is null ? ProductionReservations.Enter(null)
            : ProductionReservations.EnterFactory(await new FactoryRegistry(factoryDirectory).LoadAsync(scope.WorldId, token));
        // Production recollects inventory, recipes and geometry before acting; the LLM context is never a precondition.
        if (goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.FluidUnits)
            return new(goal, Fluid: await new FluidProductionController(game, journal).RunAsync(goal.Target, (double)goal.Quantity, token));
        if (goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.ItemsPerMinute)
        {
            var director = new FactoryDirector(game, journal, factoryDirectory!);
            var plan = await director.AutomateAsync(goal.Target, (double)goal.Quantity, token);
            await director.EnsureSupplyLineAsync(token);
            var service = await new FactoryLogistics(game, journal, factoryDirectory!).ServiceAsync(40, token, usePlannedBuffers: true);
            return new(goal, Automation: plan, Logistics: service);
        }
        if (goal.Category == GoalCategory.Research)
            return new(goal, Research: await new ResearchGoalExecutor(game, journal, factoryDirectory: factoryDirectory).RunAsync(goal.Target, token));
        if (goal.Category == GoalCategory.Exploration)
        {
            var attempt = await new ResourceDiscoveryController(game, journal, factoryDirectory).SearchAsync(goal.Target, token);
            return new(goal, Discovery: attempt.Discovery, SearchProgress: attempt.Progress);
        }
        if (goal.Category == GoalCategory.Launch)
            return new(goal, Rocket: await new RocketLaunchController(game, journal, factoryDirectory).RunAsync(goal.Target, token));
        if (goal.Category == GoalCategory.Defense && catalog.Items[goal.Target].PlaceEntityType == "wall")
            return new(goal, Perimeter: await new PerimeterDefenseController(game, journal, factoryDirectory!).RunAsync(goal.Target, token: token));
        if (goal.Category == GoalCategory.Defense)
            return new(goal, Defense: await new DefenseDeploymentController(game, journal).RunAsync(goal.Target, (int)goal.Quantity, token));
        return new(goal, Production: await new ProductionGoalExecutor(game, journal).RunAsync(goal.Target, (int)goal.Quantity, token));
    }

    /// <summary>Journals the decision model's view of this proposal. It never changes the goal or delays defense.</summary>
    private async Task ShadowAsync(string facts, string? previousResult, GoalProposal goal, CancellationToken token)
    {
        var defense = new DefenseController(game, journal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        object entry;
        try
        {
            Task<ShadowVerdict> pending = DecisionShadow.AskAsync(shadow!, DecisionShadow.Build(facts, previousResult, goal), deadline.Token);
            try
            {
                while (!pending.IsCompleted)
                {
                    await defense.StepAsync(token);
                    await Task.WhenAny(pending, Task.Delay(150, token));
                }
                entry = await pending;
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await defense.StopOwnedActionAsync(stop.Token);
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not GameRpcException)
        {
            entry = new { error = error.GetType().Name, error.Message };
        }
        await journal.AppendAsync("decision-shadow", entry, token);
    }

    /// <summary>Known factory inventory totals, actor included, plus installed turrets counted under their item.</summary>
    internal static IReadOnlyDictionary<string, long> Obtained(FactorySnapshot factory, DefenseFactoryState? defenses, ProductionCatalog catalog)
    {
        var stock = new Dictionary<string, long>(factory.SummarizeStocks().InventoryItems, StringComparer.Ordinal);
        foreach (var turret in defenses?.Turrets ?? [])
            if (catalog.Turrets?.FirstOrDefault(t => t.Value.EntityName == turret.Name).Key is { } item)
                stock[item] = stock.GetValueOrDefault(item) + 1;
        return stock;
    }

    /// <summary>
    /// Stock holds obtained items (see <see cref="Obtained"/>): turrets can be installed only when enough are obtained or their
    /// recipe is enabled, and a perimeter needs an obtainable wall and turret.
    /// </summary>
    public static string? GroundingFailure(GoalProposal goal, string observationId, ProductionCatalog catalog,
        IReadOnlyDictionary<string, NativeTechnology>? technologies = null, bool automation = false, bool factory = false,
        IReadOnlyDictionary<string, long>? stock = null)
    {
        bool Obtainable(string item) => stock?.GetValueOrDefault(item) > 0 || FactoryDirector.Enabled(catalog, item);
        if (goal.ObservationId != observationId) return "The proposal references a different observation.";
        if (goal.Category == GoalCategory.Exploration)
        {
            if (goal.Unit != GoalUnit.Completion || goal.Quantity != 1) return "Resource exploration requires a completion goal with quantity 1.";
            return ResourceDiscoveryController.Supported(catalog, goal.Target) ? null
                : "Exploration requires an exact observed native resource identifier; aliases and arbitrary area descriptions are unsupported.";
        }
        if (goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.ItemsPerMinute)
        {
            if (!automation) return "Automation needs enabled assembler, inserter, pole and lab recipes and a factory directory.";
            if (goal.Quantity is <= 0 or > 600) return "Automation requires a rate above zero and at most 600 items per minute.";
            if (catalog.Items.ContainsKey(goal.Target) && ResourceCellPlanner.Supply(catalog, goal.Target) is not null)
                return factory ? null : "Persistent resource-rate goals require the factory directory.";
            return AutomationPlanner.Choose(catalog, goal.Target, FactoryDirector.MachineItems(catalog)) is null
                && FluidChainPlanner.Choose(catalog, goal.Target, FluidChainDirector.Machines(catalog)) is not { Recipe.Products: [{ DeterministicItem: true }] }
                ? "The target has no supported deterministic native resource cell, enabled solid assembler or furnace-band recipe, nor a fluid chain with a solid product." : null;
        }
        if (goal.Category == GoalCategory.Defense)
        {
            if (catalog.Items.TryGetValue(goal.Target, out var wall) && wall.PlaceEntityType == "wall")
            {
                if (goal.Unit != GoalUnit.Completion || goal.Quantity != 1)
                    return "Perimeter walls use a completion goal with quantity 1; C# sizes turrets and walls from known industry.";
                if (!factory) return "Perimeter walls need the persistent factory registry.";
                if (catalog.Turrets is not { Count: > 0 }) return "Perimeter walls need a supported native ammunition turret.";
                return Obtainable(goal.Target) && catalog.Turrets.Keys.Any(Obtainable) ? null
                    : "Perimeter walls need a wall and a supported turret that are stocked or craftable; research them first.";
            }
            if (goal.Unit != GoalUnit.Items || goal.Quantity is < 1 or > 32 || decimal.Truncate(goal.Quantity) != goal.Quantity)
                return "Defense requires 1 to 32 whole installed ammunition turrets, unit items.";
            if (!catalog.Items.TryGetValue(goal.Target, out var turret) || turret.PlaceEntityType != "ammo-turret"
                || catalog.Turrets is null || !catalog.Turrets.TryGetValue(goal.Target, out var supported) || supported.EntityName != turret.PlaceEntity)
                return "Defense requires an exact supported native ammunition-turret item identifier.";
            return stock?.GetValueOrDefault(goal.Target) >= goal.Quantity || FactoryDirector.Enabled(catalog, goal.Target) ? null
                : $"Fewer than {goal.Quantity} {goal.Target} are installed or stocked and its recipe is not enabled; research its technology first.";
        }
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

    /// <summary>
    /// Recipes a technology unlocks, from its native effects, so the planner can tell which research brings a better gun,
    /// armor, ammunition or wall. On 2026-10-01 (seed 20261002) the pistol-armed actor died 23 times while military research
    /// stayed unchosen.
    /// </summary>
    internal static string[] Unlocks(JsonElement? effects) => TechnologyPlanner.RecipeUnlocks(effects).Take(12).ToArray();

    private static string[] Names(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).Distinct(StringComparer.Ordinal).Order().ToArray()
        : [];
}
