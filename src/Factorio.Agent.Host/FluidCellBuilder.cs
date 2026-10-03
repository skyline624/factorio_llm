using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds persistent fluid cells outside factory bands: extractors on fluid deposits, and fluid machines with their pole,
/// chest-fed inserters when the recipe moves solids, C#-routed pipes and power links. Every id comes from a build receipt and
/// every role keeps its planned position, so maintenance can rebuild it. Sites, pipes, pumps and links keep off the ground
/// reserved for bands, resource rows and steam growth, and poles are linked outward from the fed network wherever it stands.
/// An interrupted cell resumes where it stands, its lost parts rebuilt at their plan, and is abandoned once its build attempts
/// are spent. An extractor feeds exactly one machine, so each new machine consuming an extracted fluid is paired with a free one.
/// </summary>
public sealed class FluidCellBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    public const string ExtractorKind = "extractor";
    public const string MachineKind = AutomationPlanner.FluidKind;
    public const int MaximumExtractorSearchSteps = 64;
    // The machine is committed before the small parts whose approach must avoid its footprint.
    private static readonly string[] PartOrder = ["drill", "machine", "pole", "input-inserter", "input-chest", "output-inserter", "output-chest"];

    /// <summary>Builds a powered extractor on an observed usable deposit, searching remembered sites and bounded safe frontiers when needed.</summary>
    public async Task<FactoryCell> BuildExtractorAsync(string resource, string product, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        token = deadline.Token;
        var catalog = await CatalogAsync(token);
        var registry = new FactoryRegistry(directory);
        string pipeItem = PipeItem(catalog);
        string[] drills = catalog.Items.Where(p => p.Value.PlaceEntityType == "mining-drill").Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        if (drills.Length is 0 or > 16) throw new InvalidOperationException("No bounded native drill catalog is available.");
        // Only the pole of this equipment matters until the site chooses the drill.
        var template = FactoryCellBuilder.Equipment(catalog, drills[0]);
        var ground = await GroundAsync(registry, catalog, token);
        await using var controller = new SpatialController(game, journal);
        var cell = await ResumeAsync(c => c.Kind == ExtractorKind && c.Recipe == product, "drill", registry, catalog, controller, token);
        if (cell is null)
        {
            var (site, drillItem, tick) = await FindExtractorAsync(resource, template, pipeItem, [.. drills, template.Pole, pipeItem], ground,
                catalog, controller, token);
            await RequireFedPoleAsync(site.Layout, catalog, token);
            cell = new($"fluid-{Guid.NewGuid():N}", 0, new(0, 0, true), ExtractorKind, drillItem, product, new Dictionary<string, string>(),
                "building", tick, Attempts: 1, Plan: Roles(site.Layout, "drill"));
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-extractor-plan", new { cell.Id, resource, product, site, tick }, token);
        }
        var equipment = FactoryCellBuilder.Equipment(catalog, cell.MachineItem);
        cell = await PlaceAsync(cell, registry, catalog, controller, token);
        cell = await PowerAsync(cell, registry, catalog, equipment, ground, controller, token);
        // Exploration and a long grid extension can exhaust steam fuel before this consumer starts.
        // Reuse the normal native supply controller rather than waiting on an idle connected network.
        await new PoweredMachineController(game, journal).MaintainFuelAsync(cell.Entities["drill"], 0,
            catalog, controller, reserve: false, token: token);
        // Native stock at the extractor proves power, deposit and port before any machine is planned against it.
        await WaitForStockAsync(product, cell.Entities["drill"], controller, catalog, token);
        return await ReadyAsync(registry, catalog, cell, token);
    }

    /// <summary>
    /// Builds one machine cell for the recipe beside its fluid supply: a free extractor for extracted fluids, the nearest
    /// native stock otherwise, and a new offshore pump for a terrain fluid without an observed source.
    /// </summary>
    public async Task<FactoryCell> BuildMachineAsync(string machineItem, string recipeName, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(60));
        token = deadline.Token;
        var catalog = await CatalogAsync(token);
        var recipe = catalog.Recipes.SingleOrDefault(r => r.Name == recipeName && r.Enabled)
            ?? throw new InvalidOperationException($"{recipeName} is not an enabled native recipe.");
        if (recipe.Ingredients.Any(i => i.DeterministicFluid && (i.Temperature is not null || i.MinimumTemperature is not null || i.MaximumTemperature is not null)))
            throw new InvalidOperationException("Temperature-constrained fluid inputs require a thermal supply plan.");
        bool input = recipe.Ingredients.Any(i => i.DeterministicItem), output = recipe.Products.Any(p => p.DeterministicItem);
        string[] fluids = recipe.Ingredients.Where(i => i.DeterministicFluid).Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
        var equipment = FactoryCellBuilder.Equipment(catalog, machineItem);
        string pipeItem = PipeItem(catalog);
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        string[] pumps = catalog.Items.Where(p => p.Value.PlaceEntityType == "offshore-pump"
            && (carried.GetValueOrDefault(p.Key) > 0 || FactoryDirector.Enabled(catalog, p.Key))).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var registry = new FactoryRegistry(directory);
        var ground = await GroundAsync(registry, catalog, token);
        string[] items = [machineItem, equipment.Inserter, equipment.Chest, equipment.Pole, pipeItem, .. pumps, .. ground.Items];
        await using var controller = new SpatialController(game, journal);
        var cell = await ResumeAsync(c => c.Kind == MachineKind && c.MachineItem == machineItem && c.Recipe == recipeName, "machine",
            registry, catalog, controller, token);
        if (cell is null)
        {
            var sources = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string fluid in fluids)
                sources[fluid] = FluidChainPlanner.Resource(catalog, fluid) is null ? null : await FreeExtractorAsync(fluid, null, catalog, controller, token);
            foreach (string fluid in fluids.Where(f => !FluidChainPlanner.Terrain(catalog, f)))
                await WaitForStockAsync(fluid, sources[fluid], controller, catalog, token);
            var anchor = await AnchorAsync(fluids, sources, catalog, token);
            await controller.TravelAsync(anchor, 6, catalog, token);
            var map = await CaptureAsync(items, catalog, token);
            var stock = await SnapshotAsync(catalog, token);
            // A terrain fluid no observed pump draws yet gets a new pump beside the machine, so the machine moves to that shore.
            string[] pumped = MissingTerrainSupply(map, stock, fluids, catalog);
            if (pumped.Length > 0)
            {
                var shore = Shore(map, pumped[0], anchor);
                if (shore is null)
                {
                    await new FluidRelayController(game, journal, directory).ExtendAsync(pumped[0], anchor, catalog, controller, ground, token);
                    await controller.TravelAsync(anchor, 6, catalog, token);
                    map = await CaptureAsync(items, catalog, token);
                    stock = await SnapshotAsync(catalog, token);
                    pumped = MissingTerrainSupply(map, stock, fluids, catalog);
                    if (pumped.Length > 0) throw new InvalidOperationException("The extended terrain supply is not observed near the fluid machine site.");
                }
                else if (shore.DistanceTo(anchor) > 8)
                {
                    anchor = shore;
                    await controller.TravelAsync(anchor, 6, catalog, token);
                    map = await CaptureAsync(items, catalog, token);
                }
            }
            stock = await SnapshotAsync(catalog, token);
            string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
            // Bands, resource rows and steam growth stay free of the machine, its parts, its pipes and a new pump.
            var planning = FactoryGround.Reserve(map, ground.Boxes(map), pipeItem);
            FluidSupplyRoute? Route(SpatialSnapshot current, string fluid, CancellationToken routeToken)
            {
                if (sources.GetValueOrDefault(fluid) is { } source)
                    return new PipeRoutePlanner().Find(current, pipeItem, source, FluidCellPlanner.PlannedId, fluid, cancellationToken: routeToken) is { Status: PipeRouteStatus.Found } route
                        ? new(source, route) : null;
                if (pumped.Contains(fluid))
                    return pumps.Select(pump => new OffshoreSupplyPlanner().Find(current, pump, pipeItem, FluidCellPlanner.PlannedId, fluid, routeToken))
                        .FirstOrDefault(p => p is not null) is { } offshore ? new("planned:offshore-supply", offshore.Route) : null;
                return new FluidSupplyPlanner().Find(current, stock, pipeItem, FluidCellPlanner.PlannedId, fluid, routeToken);
            }
            var site = await ControllerPlanning.RunAsync(t => new FluidCellPlanner().Find(planning, force, equipment, pipeItem, anchor, input, output,
                    fluids, (current, fluid) => Route(current, fluid, t), cancellationToken: t), controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException($"No clear site near the {string.Join(", ", fluids)} supply routes every port assignment of {recipeName}.");
            await RequireFedPoleAsync(site.Layout, catalog, token);
            cell = new($"fluid-{Guid.NewGuid():N}", 0, new(0, 0, true), MachineKind, machineItem, recipeName, new Dictionary<string, string>(),
                "building", map.CollectedTick, Attempts: 1, Plan: Roles(site.Layout, "machine"));
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-cell-plan", new { cell.Id, recipe = recipeName, sources, pumped, anchor, site, map.CollectedTick }, token);
        }
        cell = await PlaceAsync(cell, registry, catalog, controller, token);
        await ConfigureAsync(cell.Entities["machine"], cell.Plan!["machine"], recipeName, catalog, controller, token);
        cell = await ConnectFluidsAsync(cell, fluids, ground, registry, catalog, controller, pipeItem, token);
        cell = await PowerAsync(cell, registry, catalog, equipment, ground, controller, token);
        var snapshot = await SnapshotAsync(catalog, token);
        if (FactoryPower.IsFed(snapshot, cell.Entities["machine"]) == false)
            throw new InvalidOperationException("The fluid machine's network has no power source.");
        if (recipe.Products.Count > 1)
            cell = await ReservoirsAsync(cell, recipe, registry, catalog, controller, ground, pipeItem, token);
        return await ReadyAsync(registry, catalog, cell, token);
    }

    /// <summary>Every simultaneous fluid output gets native tank storage before its consumers are constructed.</summary>
    private async Task<FactoryCell> ReservoirsAsync(FactoryCell cell, NativeRecipe recipe, FactoryRegistry registry,
        ProductionCatalog catalog, SpatialController controller, FactoryGround ground, string pipeItem, CancellationToken token)
    {
        if (!FluidChainPlanner.SupportedProducts(recipe, catalog) || recipe.Products.Any(p => !p.DeterministicFluid))
            throw new InvalidOperationException("Reservoir construction supports deterministic fluid co-products only.");
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        string tankItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "storage-tank"
            && (carried.GetValueOrDefault(p.Key) > 0 || FactoryDirector.Enabled(catalog, p.Key)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault()
            ?? throw new InvalidOperationException("Fluid co-products require an obtainable native storage tank.");
        var builder = new FactoryCellBuilder(game, journal, directory);
        string machineId = cell.Entities["machine"];
        IReadOnlyList<PlannedFluidReservoir>? joint = null;
        if (!cell.Plan!.Keys.Any(r => r.StartsWith("reservoir-", StringComparison.Ordinal)))
        {
            foreach (var product in recipe.Products) await WaitForStockAsync(product.Name, machineId, controller, catalog, token);
            await controller.TravelAsync(cell.Plan["machine"].Position, 6, catalog, token);
            var map = await CaptureAsync([tankItem, pipeItem, .. ground.Items], catalog, token);
            joint = await ControllerPlanning.RunAsync(t => new FluidReservoirPlanner().FindAll(
                FactoryGround.Reserve(map, ground.Boxes(map), pipeItem), tankItem, pipeItem, machineId,
                recipe.Products.Select(p => p.Name).ToArray(), t), controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException("No joint isolated reservoir layout for the co-products; preserve the partial cell.");
            var plans = new Dictionary<string, PlannedEntity>(cell.Plan, StringComparer.Ordinal);
            for (int i = 0; i < recipe.Products.Count; i++)
            {
                var site = joint.Single(p => p.Fluid == recipe.Products[i].Name).Site;
                plans[$"reservoir-{i}"] = new($"reservoir-{i}", tankItem, site.Tank.Position, site.Tank.Direction);
            }
            cell = cell with { Plan = plans };
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-reservoir-plan", new { cell.Id, joint }, token);
        }
        for (int index = 0; index < recipe.Products.Count; index++)
        {
            string fluid = recipe.Products[index].Name, role = $"reservoir-{index}";
            await WaitForStockAsync(fluid, machineId, controller, catalog, token);
            await controller.TravelAsync(cell.Plan!["machine"].Position, 6, catalog, token);
            var map = await CaptureAsync([tankItem, pipeItem, .. ground.Items], catalog, token);
            string? tankId = cell.Entities.GetValueOrDefault(role);
            if (tankId is null)
            {
                var site = joint?.Single(p => p.Fluid == fluid).Site ?? await ControllerPlanning.RunAsync(t => new FluidReservoirPlanner().Find(
                    FactoryGround.Reserve(map, ground.Boxes(map), pipeItem), tankItem, pipeItem, machineId, fluid, t),
                    controller, TimeSpan.FromMinutes(2), token)
                    ?? throw new InvalidOperationException($"No safe native reservoir site for {fluid}; preserve the partial cell.");
                var planned = new PlannedEntity(role, tankItem, site.Tank.Position, site.Tank.Direction);
                cell = cell with { Plan = new Dictionary<string, PlannedEntity>(cell.Plan!, StringComparer.Ordinal) { [role] = planned } };
                await SaveAsync(registry, catalog, cell, token);
                await builder.EnsureCarriedAsync(registry, catalog, tankItem, 1, token);
                tankId = await new PoweredMachineController(game, journal).BuildAtAsync(tankItem, site.Tank, catalog, controller, token,
                    site.Route.Pipes.Append(cell.Plan["machine"].Position).ToArray());
                cell = WithRole(cell, role, tankId, planned);
                await SaveAsync(registry, catalog, cell, token);
            }
            map = await CaptureAsync([tankItem, pipeItem, .. ground.Items], catalog, token);
            if (!FluidBufferPlanner.ConnectedStorageIds(map, machineId, fluid).Contains(tankId))
            {
                var planning = FactoryGround.Reserve(map, ground.Boxes(map), pipeItem);
                string source = FluidBufferPlanner.ConnectedStorageIds(map, machineId, fluid).Append(machineId)
                    .Order(StringComparer.Ordinal).FirstOrDefault(id => new PipeRoutePlanner().Find(planning, pipeItem, id, tankId, fluid,
                        cancellationToken: token).Status == PipeRouteStatus.Found)
                    ?? throw new InvalidOperationException($"The planned {fluid} reservoir has no safe connection; reconcile the partial cell.");
                using (ProductionReservations.EnterFactory(await registry.LoadAsync(catalog.Scope.WorldId, token)))
                {
                    var result = await new PipeConnectionController(game, journal).RunAsync(source, tankId, fluid, token,
                        current =>
                        {
                            var forecast = FactoryGround.Reserve(current, ground.Boxes(current), pipeItem);
                            foreach (var other in joint?.Where(p => p.Fluid != fluid) ?? [])
                                forecast = FluidReservoirPlanner.Project(forecast, tankItem, pipeItem, other);
                            return forecast with { Actor = current.Actor };
                        }, geometryItems: [tankItem, .. ground.Items]);
                    cell = WithPipes(cell, await SnapshotAsync(catalog, token), result.BuiltPipeIds, pipeItem);
                }
                await SaveAsync(registry, catalog, cell, token);
            }
            map = await CaptureAsync([tankItem, pipeItem], catalog, token);
            var snapshot = await SnapshotAsync(catalog, token);
            var records = snapshot.FluidRecordsAt(tankId).ToArray();
            double capacity = records.Sum(r => r.Data.GetProperty("capacity").GetDouble());
            if (!FluidBufferPlanner.ConnectedStorageIds(map, machineId, fluid).Contains(tankId)
                || !double.IsFinite(capacity) || capacity <= 0 || records.Any(r => r.Data.GetProperty("contents").EnumerateObject()
                    .Any(p => p.Name != fluid && p.Value.GetDouble() > 0)))
                throw new InvalidDataException("The native reservoir connection, capacity or fluid isolation was not proven.");
            await journal.AppendAsync("fluid-reservoir-connected", new { cell.Id, machineId, tankId, fluid, capacity,
                amount = snapshot.FluidStockAt(tankId, fluid), snapshot.CollectedTick, isFiniteStorage = true }, token);
        }
        return cell;
    }

    /// <summary>
    /// Native output rate of every ready extractor cell of a fluid, read from the deposit under its drill. Cells whose drill or
    /// deposit is not observed after travelling to it count as zero; at most sixteen cells are visited.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> ExtractorRatesAsync(string product, CancellationToken token = default)
    {
        var catalog = await CatalogAsync(token);
        var registry = new FactoryRegistry(directory);
        await using var controller = new SpatialController(game, journal);
        await AdoptExtractorsAsync(product, catalog, registry, controller, token);
        var cells = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells
            .Where(c => c.Kind == ExtractorKind && c.Status == "ready" && c.Recipe == product && c.Entities.ContainsKey("drill") && c.Plan?.ContainsKey("drill") == true)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Take(16).ToArray();
        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (rates.ContainsKey(cell.Id)) continue;
            await controller.TravelAsync(cell.Plan!["drill"].Position, 8, catalog, token);
            var map = await CaptureAsync([cell.MachineItem], catalog, token);
            foreach (var other in cells.Where(c => !rates.ContainsKey(c.Id)))
                if (Rate(map, catalog, other) is { } rate) rates[other.Id] = rate;
            rates.TryAdd(cell.Id, 0);
        }
        await journal.AppendAsync("fluid-extractor-rates", new { product, rates }, token);
        return rates;
    }

    private async Task AdoptExtractorsAsync(string product, ProductionCatalog catalog, FactoryRegistry registry,
        SpatialController controller, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(catalog, token);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var candidates = FluidExtractorAdoption.Candidates(state, snapshot, product);
        if (candidates.Count == 0) return;
        var map = await CaptureAsync([], catalog, token);
        foreach (var candidate in candidates.OrderBy(r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!
            .DistanceTo(map.Actor.Position)).ThenBy(r => r.EntityId, StringComparer.Ordinal).Take(FluidExtractorAdoption.MaximumCandidates))
        {
            await controller.TravelAsync(candidate.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, 8, catalog, token);
            map = await CaptureAsync([], catalog, token);
            snapshot = await SnapshotAsync(catalog, token);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var cell = FluidExtractorAdoption.Plan(state, snapshot, map, catalog, product, candidate.EntityId);
            if (cell is null) continue;
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-extractor-adopted", new { cell.Id, product, drillId = candidate.EntityId, cell.Entities,
                cell.Plan, map.CollectedTick, snapshotTick = snapshot.CollectedTick,
                stock = snapshot.FluidStockAt(candidate.EntityId, product), perMinute = Rate(map, catalog, cell) }, token);
        }
    }

    /// <summary>The deposit yield of an extractor observed on the map, or null when its drill or deposit is out of view.</summary>
    internal static double? Rate(SpatialSnapshot map, ProductionCatalog catalog, FactoryCell cell)
    {
        var drill = map.Entities.SingleOrDefault(e => e.Id == cell.Entities["drill"]);
        if (drill is null) return null;
        var deposit = map.Entities.Where(e => map.Prototypes[e.Name].Type == "resource" && e.Amount > 0 && e.Bounds.Contains(drill.Position)
                && catalog.Mining.TryGetValue(e.Name, out var products) && products.Any(p => p.Name == cell.Recipe && p.DeterministicFluid))
            .OrderBy(e => e.Position.DistanceTo(drill.Position)).FirstOrDefault();
        return deposit is null ? null : FluidChainPlanner.ExtractorPerMinute(map.Prototypes[drill.Name], map.Prototypes[deposit.Name],
            deposit.Amount!.Value, catalog.Mining[deposit.Name].Single(p => p.Name == cell.Recipe));
    }

    private async Task<(ExtractorSite Site, string Item, long Tick)> FindExtractorAsync(string resource, CellEquipment template, string pipeItem,
        string[] items, FactoryGround ground, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var charting = new ChartedResourceSurvey(game, journal);
        var exploration = new ExplorationPlanner();
        for (int attempt = 0; attempt < MaximumExtractorSearchSteps; attempt++)
        {
            // Deposits on the force's map join the remembered destinations below, before the local view is taken.
            await charting.BeforeExplorationAsync([resource], "fluid-extractor-search", token);
            var map = await CaptureAsync([.. items, .. ground.Items], catalog, token);
            // A deposit under a band, a resource row or steam growth stays free for them.
            var planning = FactoryGround.Reserve(map, ground.Boxes(map), pipeItem);
            var candidates = items.Where(i => map.Items.TryGetValue(i, out var placeable)
                && map.Prototypes[placeable.EntityName] is { Type: "mining-drill", IsElectric: true } drill
                && drill.FluidBoxes?.Any(b => b.ProductionType == "output") == true
                && (carried.GetValueOrDefault(i) > 0 || FactoryDirector.Enabled(catalog, i))).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("No obtainable electric drill can extract the requested native fluid.");
            foreach (string item in candidates)
                if (new FluidCellPlanner().FindExtractor(planning, template with { Machine = item }, resource, pipeItem) is { } site)
                    return (site, item, map.CollectedTick);
            // All locally observed deposits were checked against current geometry and reserved ground. Their
            // historical sightings must not send this search back to the same occupied or unsuitable patch.
            string[] unsuitable = map.Entities.Where(e => e.Name == resource).Select(e => e.Id).ToArray();
            foreach (string id in unsuitable) visited.Add(id);
            // Remembered deposits only guide travel; geometry and amounts are observed again on arrival.
            var memory = game is IResourceMemoryReader reader ? await reader.ReadResourceMemoryAsync(map, token) : null;
            IReadOnlyList<NativeDeathTransition> deaths = game is IDangerZoneReader danger
                ? await danger.ReadActiveDeathsAsync(map.Scope, map.SurfaceIndex, map.CollectedTick, token) : [];
            var remembered = ExtractorDestination(memory, map, resource, visited, deaths);
            await journal.AppendAsync("fluid-extractor-search", new { resource, attempt, remembered, map.CollectedTick }, token);
            if (attempt == MaximumExtractorSearchSteps - 1)
                throw new FluidExtractorSearchExhaustedException(resource, map.Scope, map.CollectedTick, MaximumExtractorSearchSteps);
            if (ResourceResearchController.BlindSearchTooDangerous(remembered, deaths))
            {
                await journal.AppendAsync("fluid-extractor-search-too-dangerous", new { resource, attempt, map.Scope,
                    map.CollectedTick, deaths, failureCode = "exploration_too_dangerous" }, token);
                throw new ExplorationTooDangerousException("No safe remembered fluid deposit is available, and recent own deaths refuse blind exploration.");
            }
            await new SurvivalKitController(game, journal).BeforeTripAsync("fluid-extractor-exploration", token);
            // Empty wanted avoids selecting an occupied local deposit again; an actual historical destination
            // guides successive local steps and is reobserved, while blind steps retain normal hazard exclusions.
            var frontier = await controller.FindExplorationWaypointAsync(exploration, catalog, "", remembered?.Position, token,
                avoidDestinationDeathZones: true);
            await journal.AppendAsync("fluid-extractor-exploration", new { resource, attempt, remembered, frontier,
                deferredObservedResources = unsuitable, limit = MaximumExtractorSearchSteps }, token);
            await controller.NavigateAsync(frontier.Position, cancellationToken: token);
        }
        throw new InvalidOperationException("The extractor search ended without an observed result.");
    }

    internal static ResourceSighting? ExtractorDestination(ResourceMemorySnapshot? memory, SpatialSnapshot map, string resource,
        IReadOnlySet<string> deferred, IReadOnlyList<NativeDeathTransition> deaths)
    {
        memory?.ValidateFor(map);
        return memory?.NearestOf(resource, map.Actor.Position, r => !deferred.Contains(r.EntityId)
            && r.Position.DistanceTo(map.Actor.Position) > 24 && !deaths.Any(d => DangerZones.Covers(d, r.Position)));
    }

    /// <summary>A ready extractor of the fluid whose output port feeds nothing yet, nearest to the given point or the actor.</summary>
    private async Task<string?> FreeExtractorAsync(string fluid, MapPosition? near, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        var extractors = (await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token)).Cells
            .Where(c => c.Kind == ExtractorKind && c.Status == "ready" && c.Recipe == fluid && c.Entities.ContainsKey("drill") && c.Plan?.ContainsKey("drill") == true)
            .ToArray();
        if (extractors.Length == 0) return null;
        var reference = near ?? (await CaptureAsync([], catalog, token)).Actor.Position;
        foreach (var cell in extractors.OrderBy(c => c.Plan!["drill"].Position.DistanceTo(reference)).ThenBy(c => c.Id, StringComparer.Ordinal).Take(16))
        {
            var map = await CaptureAsync([cell.MachineItem], catalog, token);
            if (map.Entities.All(e => e.Id != cell.Entities["drill"]))
            {
                await controller.TravelAsync(cell.Plan!["drill"].Position, 8, catalog, token);
                map = await CaptureAsync([cell.MachineItem], catalog, token);
            }
            var drill = map.Entities.SingleOrDefault(e => e.Id == cell.Entities["drill"]);
            if (drill?.FluidConnections is { } ports && ports.Where(p => p.FlowDirection is "output" or "input-output").All(p => p.TargetEntityId is null))
                return drill.Id;
        }
        return null;
    }

    private async Task WaitForStockAsync(string fluid, string? holder, SpatialController controller, ProductionCatalog catalog, CancellationToken token)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var snapshot = await SnapshotAsync(catalog, token);
            double amount = holder is null ? snapshot.SummarizeStocks().Fluids.GetValueOrDefault(fluid) : snapshot.FluidStockAt(holder, fluid);
            if (amount > 0)
            {
                await journal.AppendAsync("fluid-supply-observed", new { fluid, holder, amount, attempt, snapshot.CollectedTick }, token);
                return;
            }
            var waited = await controller.WorkAsync("wait", new { ticks = 120 }, 420, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Fluid supply wait did not complete.");
        }
        throw new InvalidOperationException($"No native {fluid} stock appeared {(holder is null ? "in the known factory" : $"at {holder}")}; its supplier is not producing.");
    }

    /// <summary>Anchor on fluids that require factory supply; terrain fluids can be pumped beside that supply.</summary>
    private async Task<MapPosition> AnchorAsync(IReadOnlyList<string> fluids, IReadOnlyDictionary<string, string?> sources,
        ProductionCatalog catalog, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(catalog, token);
        return Anchor(snapshot, fluids, sources, catalog);
    }

    internal static MapPosition Anchor(FactorySnapshot snapshot, IReadOnlyList<string> fluids,
        IReadOnlyDictionary<string, string?> sources, ProductionCatalog catalog)
    {
        var actor = Position(snapshot, snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor").EntityId);
        // Native recipe order puts water before gas in sulfur. A remote steam pump must not pull the cell away
        // from its refinery: gas cannot be created at a new shore, whereas a new pump can provide water there.
        foreach (string fluid in fluids.OrderBy(f => FluidChainPlanner.Terrain(catalog, f)))
        {
            if (sources.GetValueOrDefault(fluid) is { } source) return Position(snapshot, source);
            var holders = snapshot.Records.Where(r => r.Kind == "fluid" && r.Data.GetProperty("contents").TryGetProperty(fluid, out var amount) && amount.GetDouble() > 0
                    && r.Data.TryGetProperty("sourceBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array)
                .SelectMany(r => r.Data.GetProperty("sourceBoxes").EnumerateArray().Select(b => b.GetProperty("entityId").GetString()!))
                .Where(id => snapshot.Records.Any(r => r.Kind == "entity" && r.EntityId == id)).Distinct(StringComparer.Ordinal)
                .Select(id => Position(snapshot, id)).OrderBy(p => p.DistanceTo(actor)).ToArray();
            if (holders.Length > 0) return holders[0];
        }
        return actor;
    }

    private async Task ConfigureAsync(string machineId, PlannedEntity plan, string recipe, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        var state = await new ProductionController(game, journal).ObserveAsync(token);
        if (state.Entities.SingleOrDefault(e => e.Id == machineId)?.Recipe != recipe)
        {
            await controller.ApproachEntityAsync(machineId, plan.Position, catalog, token);
            var configured = await controller.WorkAsync("set_recipe", new { entityId = machineId, recipe }, 600, token: token);
            if (configured.Status != "completed") throw new InvalidOperationException($"Fluid machine set_recipe ended with {configured.Status}: {configured.Error?.Code}.");
        }
        await new PoweredMachineController(game, journal).OrientFluidAsync(machineId, plan.Direction, catalog, controller, token);
    }

    /// <summary>
    /// Pipes every fluid input still unconnected: extracted fluids from the paired extractor, the others jointly from the nearest
    /// stocks, with a new offshore pump for a terrain fluid nothing supplies yet. Routes and pumps keep off the ground reserved
    /// around the machine. Built pipes and pumps become cell roles.
    /// </summary>
    private async Task<FactoryCell> ConnectFluidsAsync(FactoryCell cell, IReadOnlyList<string> fluids, FactoryGround ground, FactoryRegistry registry,
        ProductionCatalog catalog, SpatialController controller, string pipeItem, CancellationToken token)
    {
        string machineId = cell.Entities["machine"];
        var machinePosition = cell.Plan!["machine"].Position;
        string[] items = [pipeItem, .. ground.Items];
        var map = await CaptureAsync(items, catalog, token);
        if (map.Entities.All(e => e.Id != machineId))
        {
            // A resumed cell may lie beyond the capture around the actor.
            await controller.TravelAsync(machinePosition, 8, catalog, token);
            map = await CaptureAsync(items, catalog, token);
        }
        var machine = map.Entities.SingleOrDefault(e => e.Id == machineId)
            ?? throw new InvalidOperationException($"The fluid machine {machineId} is not observed at its planned position.");
        var reserved = ground.Boxes(map);
        SpatialSnapshot Keep(SpatialSnapshot current) => FactoryGround.Reserve(current, reserved, pipeItem);
        var pending = fluids.Where(f => machine.FluidConnections?.Any(p => p.Filter == f && p.FlowDirection is "input" or "input-output"
            && p.TargetEntityId is not null) != true).ToList();
        foreach (string fluid in pending.Where(f => FluidChainPlanner.Resource(catalog, f) is not null).ToArray())
        {
            if (await FreeExtractorAsync(fluid, machinePosition, catalog, controller, token) is not { } source) continue;
            cell = await RecordPipesAsync(cell, await new PipeConnectionController(game, journal).RunAsync(source, machineId, fluid, token, Keep,
                geometryItems: [.. ground.Items]),
                registry, catalog, pipeItem, token);
            pending.Remove(fluid);
        }
        if (pending.Count == 0) return cell;
        await controller.ApproachEntityAsync(machineId, machinePosition, catalog, token);
        map = Keep(await CaptureAsync([pipeItem], catalog, token));
        var stock = await SnapshotAsync(catalog, token);
        foreach (string terrain in pending.Where(f => FluidChainPlanner.Terrain(catalog, f)
            && new FluidSupplyPlanner().Find(map, stock, pipeItem, machineId, f, token) is null).ToArray())
        {
            string pump = await new OffshoreSupplyController(game, journal).PrepareJointSourceAsync(machineId, terrain, pending, catalog, controller,
                    token, Keep)
                ?? throw new InvalidOperationException($"No observed shore supports a {terrain} pump with every route of the machine.");
            var built = (await CaptureAsync([pipeItem], catalog, token)).Entities.Single(e => e.Id == pump);
            string item = catalog.Items.Where(p => p.Value.PlaceEntity == built.Name).Select(p => p.Key).Order(StringComparer.Ordinal).First();
            cell = WithRole(cell, "pump", pump, new("pump", item, built.Position, built.Direction));
            await SaveAsync(registry, catalog, cell, token);
        }
        map = Keep(await CaptureAsync([pipeItem], catalog, token));
        stock = await SnapshotAsync(catalog, token);
        var routes = new MultiFluidSupplyPlanner().Find(map, stock, pipeItem, machineId, pending, token)
            ?? throw new InvalidOperationException($"The fluid machine has no joint route for {string.Join(", ", pending)}.");
        await journal.AppendAsync("fluid-cell-routes", new { cell.Id, machineId, routes, stock.CollectedTick }, token);
        foreach (var route in routes)
            cell = await RecordPipesAsync(cell, await new PipeConnectionController(game, journal).RunAsync(route.Supply.SourceId, machineId, route.Fluid,
                token, Keep, geometryItems: [.. ground.Items]), registry, catalog, pipeItem, token);
        return cell;
    }

    private async Task<FactoryCell> RecordPipesAsync(FactoryCell cell, PipeConnectionResult result, FactoryRegistry registry,
        ProductionCatalog catalog, string pipeItem, CancellationToken token)
    {
        if (result.BuiltPipeIds.Count == 0) return cell;
        cell = WithPipes(cell, await SnapshotAsync(catalog, token), result.BuiltPipeIds, pipeItem);
        await SaveAsync(registry, catalog, cell, token);
        await journal.AppendAsync("fluid-cell-pipes", new { cell.Id, result.SourceId, result.TargetId, result.Fluid, result.BuiltPipeIds }, token);
        return cell;
    }

    /// <summary>
    /// The interrupted cell to finish, if any. Cells whose attempts are spent are abandoned first; the resumed one records its
    /// attempt, the actor walks back to it, and recorded parts that no longer stand are forgotten so their plan builds them again.
    /// </summary>
    private async Task<FactoryCell?> ResumeAsync(Func<FactoryCell, bool> match, string machineRole, FactoryRegistry registry,
        ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var (cell, spent) = Interrupted(await registry.LoadAsync(catalog.Scope.WorldId, token), match);
        foreach (var worn in spent)
        {
            await SaveAsync(registry, catalog, worn, token);
            await journal.AppendAsync("fluid-cell-abandoned", new { worn.Id, worn.Kind, worn.Recipe, worn.Entities, worn.Attempts }, token);
        }
        if (cell is null) return null;
        await SaveAsync(registry, catalog, cell, token);
        // Every capture covers 48 tiles around the actor, which may have left the cell since the interruption.
        await controller.TravelAsync(cell.Plan![machineRole].Position, 8, catalog, token);
        var standing = Standing(cell, FactoryMaintenance.Present(await SnapshotAsync(catalog, token)));
        await journal.AppendAsync("fluid-cell-resume", new { cell.Id, cell.Kind, cell.Recipe, cell.Attempts,
            lost = cell.Entities.Where(p => !standing.Entities.ContainsKey(p.Key)), unbuilt = Unbuilt(standing) }, token);
        if (standing.Entities.Count < cell.Entities.Count) await SaveAsync(registry, catalog, standing, token);
        return standing;
    }

    /// <summary>Builds every planned role without a standing entity, adopting one the engine already placed at its plan.</summary>
    private async Task<FactoryCell> PlaceAsync(FactoryCell cell, FactoryRegistry registry, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        var plan = cell.Plan!;
        var roles = Unbuilt(cell);
        string[] items = plan.Values.Select(p => p.Item).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var builder = new FactoryCellBuilder(game, journal, directory);
        // Every missing part is carried first, so no production trip interrupts construction.
        await builder.EnsureCarriedAsync(registry, catalog,
            roles.GroupBy(r => plan[r].Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), token);
        for (int index = 0; index < roles.Count; index++)
        {
            var planned = plan[roles[index]];
            var remaining = roles.Skip(index + 1).Select(r => plan[r].Position).ToArray();
            var map = await CaptureAsync(items, catalog, token);
            string entityName = map.Items[planned.Item].EntityName;
            // Built before an interruption: the receipt was applied but not recorded.
            string id = map.Entities.FirstOrDefault(e => e.Name == entityName && e.Position.DistanceTo(planned.Position) < .01
                    && (map.Prototypes[entityName].Type is "container" or "electric-pole" || e.Direction == planned.Direction))?.Id
                ?? await new PoweredMachineController(game, journal).BuildAtAsync(planned.Item, new(planned.Position, planned.Direction, 0),
                    catalog, controller, token, remaining);
            cell = cell with { Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [planned.Role] = id } };
            await SaveAsync(registry, catalog, cell, token);
        }
        return cell;
    }

    /// <summary>
    /// Links the cell pole outward from the fed network. Links keep off bands, rows and steam growth, giving up steam growth
    /// before the link itself, and become the cell's link-n roles so maintenance rebuilds them.
    /// </summary>
    private async Task<FactoryCell> PowerAsync(FactoryCell cell, FactoryRegistry registry, ProductionCatalog catalog, CellEquipment equipment,
        FactoryGround ground, SpatialController controller, CancellationToken token)
    {
        var bare = ground with { Steam = null };
        var builder = new FactoryCellBuilder(game, journal, directory);
        await new CellPowerLinker(game, journal).ConnectAsync(cell.Entities["pole"], cell.Plan!["pole"].Position, equipment.Pole,
            [.. new[] { equipment.Pole }.Concat(ground.Items).Distinct(StringComparer.Ordinal)], catalog, controller,
            [map => FactoryGround.Reserve(map, ground.Boxes(map), equipment.Pole), map => FactoryGround.Reserve(map, bare.Boxes(map), equipment.Pole)],
            count => builder.EnsureCarriedAsync(registry, catalog, equipment.Pole, count, token),
            async (linkId, link) =>
            {
                // The link belongs to this cell so maintenance rebuilds it when an attack cuts the cell off.
                cell = FactoryCellBuilder.WithLink(cell, linkId, link, equipment.Pole);
                await SaveAsync(registry, catalog, cell, token);
            }, token);
        return cell;
    }

    private async Task<FactoryCell> ReadyAsync(FactoryRegistry registry, ProductionCatalog catalog, FactoryCell cell, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(catalog, token);
        cell = cell with { Status = "ready", Tick = snapshot.CollectedTick };
        await SaveAsync(registry, catalog, cell, token);
        await journal.AppendAsync("fluid-cell-ready", cell, token);
        return cell;
    }

    /// <summary>A new cell is only registered when a fed pole is known to link its pole from, however far away it stands.</summary>
    private async Task RequireFedPoleAsync(CellLayout layout, ProductionCatalog catalog, CancellationToken token)
    {
        if (CellPowerLinker.NearestFedPole(await SnapshotAsync(catalog, token), layout.Role("pole")!.Position) is null)
            throw new InvalidOperationException("No fed electric pole is known to link a fluid cell from; build power first.");
    }

    private async Task<FactoryGround> GroundAsync(FactoryRegistry registry, ProductionCatalog catalog, CancellationToken token) =>
        new(await registry.LoadAsync(catalog.Scope.WorldId, token),
            await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token));

    /// <summary>
    /// The interrupted fluid cell to resume, counting one more build attempt, and the interrupted ones whose attempts are spent:
    /// those are abandoned where they stand, as resource cells are. A cell registered before attempts were counted resumes as its first.
    /// </summary>
    internal static (FactoryCell? Resume, IReadOnlyList<FactoryCell> Abandoned) Interrupted(FactoryState state, Func<FactoryCell, bool> match)
    {
        var interrupted = state.Cells.Where(c => c.Kind is ExtractorKind or MachineKind && c.Status == "building" && c.Plan is not null && match(c)).ToArray();
        var resume = interrupted.FirstOrDefault(c => c.Attempts < ResourceCellBuilder.MaximumAttempts);
        return (resume is null ? null : resume with { Attempts = resume.Attempts + 1 },
            interrupted.Where(c => c.Attempts >= ResourceCellBuilder.MaximumAttempts).Select(c => c with { Status = ResourceCellBuilder.Abandoned }).ToArray());
    }

    /// <summary>The cell without the recorded parts that no longer stand; their plan stays, so they are built again where the cell expects them.</summary>
    internal static FactoryCell Standing(FactoryCell cell, IReadOnlySet<string> present) => cell with
    {
        Entities = cell.Entities.Where(p => present.Contains(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
    };

    /// <summary>Planned roles without a standing entity: cell parts in build order first, then links, pipes and pumps by name and number.</summary>
    internal static IReadOnlyList<string> Unbuilt(FactoryCell cell) => (cell.Plan?.Keys ?? []).Where(r => !cell.Entities.ContainsKey(r))
        .OrderBy(r => PartOrder.Contains(r) ? Array.IndexOf(PartOrder, r) : PartOrder.Length)
        .ThenBy(r => Numbered(r).Prefix, StringComparer.Ordinal).ThenBy(r => Numbered(r).Index).ToArray();

    // link-10 follows link-9, so a chain is rebuilt in the order it was laid.
    private static (string Prefix, int Index) Numbered(string role)
    {
        int dash = role.LastIndexOf('-');
        return dash > 0 && int.TryParse(role.AsSpan(dash + 1), out int index) ? (role[..dash], index) : (role, -1);
    }

    /// <summary>
    /// The cell with a route's built pipes as its next pipe-n roles, placed from the whole-factory photograph, so a route
    /// reaching beyond one capture around the actor is registered whole.
    /// </summary>
    internal static FactoryCell WithPipes(FactoryCell cell, FactorySnapshot snapshot, IReadOnlyCollection<string> built, string pipeItem)
    {
        if (!built.All(FactoryMaintenance.Present(snapshot).Contains))
            throw new InvalidDataException("A built pipe is not known to the factory; reconcile the route before registering the cell.");
        int index = 0;
        foreach (string id in built.Order(StringComparer.Ordinal))
        {
            while (cell.Entities.ContainsKey($"pipe-{index}")) index++;
            cell = WithRole(cell, $"pipe-{index}", id, new($"pipe-{index}", pipeItem, Position(snapshot, id), 0));
        }
        return cell;
    }

    /// <summary>Cell plan by role; the layout's machine takes the given role (miners and extractors call it drill).</summary>
    internal static IReadOnlyDictionary<string, PlannedEntity> Roles(CellLayout layout, string machineRole) => layout.Entities
        .Select(e => e.Role == "machine" ? e with { Role = machineRole } : e).ToDictionary(e => e.Role, StringComparer.Ordinal);

    /// <summary>The observed tile of a terrain fluid nearest to a point.</summary>
    internal static MapPosition? Shore(SpatialSnapshot map, string fluid, MapPosition near) => map.Rows
        .Where(r => map.TileFluids?.GetValueOrDefault(r.Name) == fluid)
        .SelectMany(r => Enumerable.Range(r.X, r.Length).Select(x => new MapPosition(x + .5, r.Y + .5)))
        .OrderBy(p => p.DistanceTo(near)).ThenBy(p => p.Y).ThenBy(p => p.X).FirstOrDefault();

    internal static string[] MissingTerrainSupply(SpatialSnapshot map, FactorySnapshot stock, IReadOnlyList<string> fluids,
        ProductionCatalog catalog) => fluids.Where(f => FluidChainPlanner.Terrain(catalog, f) && !map.Entities.Any(e =>
            OffshoreSupplyPlanner.CanExtract(map, e.Id, f) || (e.FluidConnections ?? []).Any(p =>
                p.Type == "normal" && (p.Filter is null || p.Filter == f) && p.FlowDirection is "output" or "input-output")
                && stock.FluidStockAt(e.Id, f) > 0)).ToArray();

    private static FactoryCell WithRole(FactoryCell cell, string role, string id, PlannedEntity plan) => cell with
    {
        Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [role] = id },
        Plan = new Dictionary<string, PlannedEntity>(cell.Plan ?? new Dictionary<string, PlannedEntity>(), StringComparer.Ordinal) { [role] = plan }
    };

    private static async Task SaveAsync(FactoryRegistry registry, ProductionCatalog catalog, FactoryCell cell, CancellationToken token) =>
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);

    private static string PipeItem(ProductionCatalog catalog) => catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe")
        .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault()
        ?? throw new InvalidOperationException("No ordinary pipe item is available.");

    private static MapPosition Position(FactorySnapshot snapshot, string entityId) => snapshot.Records
        .Single(r => r.Kind == "entity" && r.EntityId == entityId).Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;

    private async Task<ProductionCatalog> CatalogAsync(CancellationToken token) =>
        ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));

    private async Task<SpatialSnapshot> CaptureAsync(IReadOnlyList<string> items, ProductionCatalog catalog, CancellationToken token)
    {
        string[] requested = items.Distinct(StringComparer.Ordinal).ToArray();
        // One native observation carries the geometry of at most sixteen items.
        if (requested.Length > 16) throw new InvalidOperationException("Too many cell and reserved-ground items for one native observation.");
        var map = await new SpatialClient(game).CaptureAsync(requested, 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a fluid cell; reconcile partial construction.");
        return map;
    }

    private async Task<FactorySnapshot> SnapshotAsync(ProductionCatalog catalog, CancellationToken token)
    {
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a fluid cell; reconcile partial construction.");
        return snapshot;
    }
}
