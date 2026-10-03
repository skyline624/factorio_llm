using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ResourceResearchResult(string Technology, string Resource, string MachineId,
    long StartTick, long EndTick, int PoweredSamples, double ConnectedFluidStock);

/// <summary>A blind search step refused because recent own deaths show the unknown ground is too dangerous for now.</summary>
public sealed class ExplorationTooDangerousException(string message) : InvalidOperationException(message);

/// <summary>
/// Unlocks a native resource trigger through real powered extraction, never a research grant. The deposit is searched in local
/// views, then in the resource memory, which a reading of the force's map feeds before any blind step; with a factory directory,
/// a resource neither remembered nor charted first gets the factory radar and a bounded wait for its sectors. After repeated
/// recent own deaths, a blind step is refused instead: the radar charts while the strategic layer chooses other goals.
/// </summary>
public sealed class ResourceResearchController(IGameClient game, IControllerJournal journal, string? factoryDirectory = null)
{
    /// <summary>
    /// Active own death zones from which a blind search step is refused. On 2026-10-01 (seed 20261002, run 22) the crude-oil search
    /// explored frontiers west, east and north in turn; the actor, in light armor with a pistol, died on each.
    /// </summary>
    public const int BlindSearchDeathLimit = 2;

    internal static bool BlindSearchTooDangerous(ResourceSighting? destination, IReadOnlyCollection<NativeDeathTransition> activeDeaths) =>
        destination is null && activeDeaths.Count >= BlindSearchDeathLimit;

    public async Task<ResourceResearchResult> RunAsync(string technology, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var technologyClient = new TechnologyClient(game);
        TechnologyObservation initial = await technologyClient.ReadDependenciesAsync(technology, token);
        TechnologyStep step = new TechnologyPlanner().Next(technology, initial.Technologies);
        if (step.Kind != "mine-trigger" || step.Technology != technology || step.Entity is null)
            throw new InvalidOperationException("Expected an available native resource extraction trigger.");
        string resourceName = step.Entity;
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(catalog.Scope);
        if (!catalog.Mining.TryGetValue(resourceName, out var products) || products.Length != 1
            || products[0].Type != "fluid" || products[0].Amount is not > 0 || !double.IsFinite(products[0].Amount!.Value) || products[0].Probability is not (null or 1))
            throw new InvalidOperationException("This resource trigger requires an extraction method that is not yet supported.");
        string fluidName = products[0].Name;
        string[] items = catalog.Items.Where(p => p.Value.PlaceEntityType is "mining-drill" or "electric-pole")
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        if (items.Length > 16) throw new InvalidOperationException("Extraction prototype catalog exceeds its observation budget.");
        var spatial = new SpatialClient(game);
        var production = new ProductionController(game, journal);
        var executor = new ProductionGoalExecutor(game, journal);
        var power = new PoweredMachineController(game, journal);
        var exploration = new ExplorationPlanner();
        var charting = new ChartedResourceSurvey(game, journal);
        bool radarConsulted = false;
        await using var controller = new SpatialController(game, journal);
        SpatialSnapshot map = await MapAsync();
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        ExtractionSelection? selected = null;
        // Historical resource positions only guide travel. Current geometry and amount are always read again.
        for (int search = 0; search < 64; search++)
        {
            selected = await SelectAsync();
            if (selected is not null) break;
            string[] unsuitable = map.Entities.Where(IsTarget).Select(e => e.Id).ToArray();
            foreach (string id in unsuitable) deferred.Add(id);
            // The force's map is read before a blind step; its deposits join the memory as charted destinations. The view is
            // captured again so that the memory is never newer than the observation it is read against.
            var charted = await charting.BeforeExplorationAsync([resourceName], "resource-research", token);
            if (charted is not null) map = await MapAsync();
            var (historical, deaths) = await HistoricalAsync();
            FactoryCell? radar = null;
            if (historical is null && factoryDirectory is not null && !radarConsulted)
            {
                // Neither remembered nor charted: the factory radar charts sectors, during a bounded wait before any blind walk,
                // or while other goals run when recent deaths already refuse that walk.
                radarConsulted = true;
                charted ??= await charting.ReadAsync([resourceName], "resource-research", token);
                var radars = new RadarController(game, journal, factoryDirectory);
                try
                {
                    if (BlindSearchTooDangerous(historical, deaths)) radar = await radars.EnsureAsync(catalog, controller, token);
                    else if (await radars.ChartAsync([resourceName], charted, charting, catalog, controller, token) is not null)
                    {
                        map = await MapAsync();
                        (historical, deaths) = await HistoricalAsync();
                    }
                }
                catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
                {
                    // The radar only helps the search: a refused or failed construction leaves it to its other rules.
                    await journal.AppendAsync("radar-failed", new { resourceName, error = error.GetType().Name, error.Message }, token);
                }
            }
            if (BlindSearchTooDangerous(historical, deaths))
            {
                await journal.AppendAsync("resource-research-too-dangerous", new { technology, resourceName, map.Scope, map.CollectedTick,
                    map.Actor.Position, deaths, limit = BlindSearchDeathLimit, radar = radar?.Entities.GetValueOrDefault("machine"),
                    failureCode = "exploration_too_dangerous" }, token);
                throw new ExplorationTooDangerousException($"No remembered or charted {resourceName} deposit is a destination, and "
                    + $"{deaths.Count} own deaths of the last {DangerZones.LifetimeTicks / 3600} minutes refuse a blind search step; "
                    + "the force's map is read again on the next attempt.");
            }
            // Run 16 (2026-10-01, seed 20261002): the crude-oil search met a pack far east while the actor was unarmored.
            await new SurvivalKitController(game, journal).BeforeTripAsync("resource-research-search", token);
            var frontier = await controller.FindExplorationWaypointAsync(exploration, catalog, "", historical?.Position, token,
                avoidDestinationDeathZones: true);
            await journal.AppendAsync("resource-research-search", new { resourceName, historical, frontier,
                deferredObservedResources = unsuitable }, token);
            await controller.NavigateAsync(frontier.Position, cancellationToken: token);
            map = await MapAsync();
        }
        if (selected is null) throw new InvalidOperationException("Resource discovery exhausted its local exploration budget without a usable site.");
        string machineId;
        if (selected.Site is { } site)
        {
            await journal.AppendAsync("resource-research-grid-placement", new
                { technology, resourceName, selected.Item, site, map.Scope, map.CollectedTick }, token);
            if (site.ExistingMachineId is { } installed) machineId = installed;
            else
            {
                await executor.RunAsync(selected.Item, 1, token);
                machineId = await power.BuildAtAsync(selected.Item, site.Machine, catalog, controller, token);
            }
            await new PowerGridController(game, journal).ConnectAsync(machineId, catalog, controller, token);
        }
        else
        {
            ResourceExtractionPlacement plan = selected.Local!;
            await journal.AppendAsync("resource-research-placement", new { technology, resourceName, selected.Item, selected.Pole, plan, map.Scope, map.CollectedTick }, token);
            await executor.RunAsync(selected.Item, 1, token);
            if (plan.AdditionalPole is not null) await executor.RunAsync(selected.Pole!, 1, token);
            string sourceId = plan.PoleId;
            if (plan.AdditionalPole is not null)
            {
                string addedId = await power.BuildAtAsync(selected.Pole!, plan.AdditionalPole, catalog, controller, token);
                map = await MapAsync();
                if (Network(map, addedId) is not { } network || network != Network(map, sourceId))
                    throw new InvalidDataException("The extraction pole did not connect to the planned source network.");
                sourceId = addedId;
            }
            machineId = await power.BuildAtAsync(selected.Item, plan.Machine, catalog, controller, token);
            map = await MapAsync();
            if (Network(map, machineId) is not { } localNetwork || localNetwork != Network(map, sourceId))
                throw new InvalidDataException("The resource extractor has not joined the expected native electric network.");
        }
        map = await MapAsync();
        long machineNetwork = Network(map, machineId)
            ?? throw new InvalidDataException("The resource extractor has no verified native electric network.");
        await power.MaintainFuelAsync(machineId, 0, catalog, controller, false, token);
        int powered = 0;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            map = await MapAsync();
            var machine = map.Entities.Single(e => e.Id == machineId);
            if (machine.Power?.Energy > 0 && machine.Power.NetworkId == machineNetwork) powered++;
            TechnologyObservation proof = await technologyClient.ReadDependenciesAsync(technology, token);
            RequireScope(proof.Scope);
            var stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(stock.Scope);
            double fluid = stock.Records.Where(r => r.Kind == "fluid" && ContainsMachine(r, machineId))
                .Sum(r => r.Data.GetProperty("contents").TryGetProperty(fluidName, out var value) ? value.GetDouble() : 0);
            await journal.AppendAsync("resource-research-measurement", new
            {
                machineId,
                fluidName,
                fluid,
                stock.SnapshotId,
                stock.CollectedTick,
                power = machine.Power,
                researched = proof.Technologies[technology].Researched
            }, token);
            if (proof.Technologies[technology].Researched && powered > 0 && fluid > 0)
            {
                var result = new ResourceResearchResult(technology, resourceName, machineId, initial.StartTick, stock.CollectedTick, powered, fluid);
                await journal.AppendAsync("resource-research-result", result, token);
                return result;
            }
            if (attempt % 10 == 0) await power.MaintainFuelAsync(machineId, 0, catalog, controller, false, token);
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Extraction observation wait did not complete; reconcile native effects.");
        }
        throw new InvalidOperationException("Powered extraction did not establish the native research and fluid evidence within its budget.");

        async Task<ExtractionSelection?> SelectAsync()
        {
            ProductionState factory = await production.ObserveAsync(token);
            RequireScope(factory.Scope);
            bool CanObtain(string item) => factory.Inventory.GetValueOrDefault(item) > 0
                || catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item && p.DeterministicItem));
            var poles = items.Where(i => CanObtain(i) && map.Prototypes[map.Items[i].EntityName].Type == "electric-pole").ToArray();
            var machines = items.Where(i => CanObtain(i) && map.Prototypes[map.Items[i].EntityName] is { Type: "mining-drill", IsElectric: true } p
                && p.FluidBoxes?.Any(b => b.ProductionType == "output") == true).ToArray();
            if (machines.Length == 0) throw new InvalidOperationException("No obtainable compatible fluid extractor is available.");
            var owned = factory.Entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var planner = new ResourceExtractionPlanner();
            var sites = machines.Select(item => new ExtractionSelection(item, Site: planner.FindSite(map, resourceName, item, owned)))
                .Where(candidate => candidate.Site is not null).ToArray();
            var existing = sites.FirstOrDefault(candidate => candidate.Site!.ExistingMachineId is not null);
            if (existing is not null) return existing;
            foreach (string item in machines)
                foreach (string pole in poles)
                    if (planner.Find(map, resourceName, item, pole, owned) is { } plan) return new(item, pole, plan);
            return sites.FirstOrDefault();
        }

        bool IsTarget(SpatialEntity entity) => entity.Name == resourceName && entity.Amount > 0 && map.Prototypes[entity.Name].Type == "resource";
        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidDataException("Actor scope changed during resource research; reconcile partial effects.");
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync(items, 48, token);
            RequireScope(value.Scope);
            return value;
        }
        // The nearest remembered deposit, locally observed or charted, that was not refused here, and the active own death zones.
        // A deposit in the zone of a recent own death is not a destination.
        async Task<(ResourceSighting? Destination, IReadOnlyList<NativeDeathTransition> Deaths)> HistoricalAsync()
        {
            ResourceMemorySnapshot? memory = game is IResourceMemoryReader reader ? await reader.ReadResourceMemoryAsync(map, token) : null;
            IReadOnlyList<NativeDeathTransition> zones = game is IDangerZoneReader danger
                ? await danger.ReadActiveDeathsAsync(map.Scope, map.SurfaceIndex, map.CollectedTick, token) : [];
            return (memory?.NearestOf(resourceName, map.Actor.Position,
                r => !deferred.Contains(r.EntityId) && !zones.Any(z => DangerZones.Covers(z, r.Position))), zones);
        }
    }
    private sealed record ExtractionSelection(string Item, string? Pole = null, ResourceExtractionPlacement? Local = null,
        ResourceExtractionSite? Site = null);
    private static long? Network(SpatialSnapshot map, string id) => map.Entities.Single(e => e.Id == id).Power?.NetworkId;
    private static bool ContainsMachine(FactoryRecord record, string id) => record.EntityId == id
        || (record.Data.TryGetProperty("sourceBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array
            && boxes.EnumerateArray().Any(b => b.GetProperty("entityId").GetString() == id));
}
