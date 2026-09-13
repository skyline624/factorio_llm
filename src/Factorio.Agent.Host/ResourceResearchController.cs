using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ResourceResearchResult(string Technology, string Resource, string MachineId,
    long StartTick, long EndTick, int PoweredSamples, double ConnectedFluidStock);

/// <summary>Unlocks a native resource trigger through real powered extraction, never a research grant.</summary>
public sealed class ResourceResearchController(IGameClient game, IControllerJournal journal)
{
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
            ResourceMemorySnapshot? memory = game is IResourceMemoryReader reader ? await reader.ReadResourceMemoryAsync(map, token) : null;
            var historical = memory?.Resources.Where(r => r.Name == resourceName && !deferred.Contains(r.EntityId))
                .OrderBy(r => r.Position.DistanceTo(map.Actor.Position)).FirstOrDefault();
            var frontier = await controller.FindExplorationWaypointAsync(exploration, catalog, "", historical?.Position, token);
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
    }
    private sealed record ExtractionSelection(string Item, string? Pole = null, ResourceExtractionPlacement? Local = null,
        ResourceExtractionSite? Site = null);
    private static long? Network(SpatialSnapshot map, string id) => map.Entities.Single(e => e.Id == id).Power?.NetworkId;
    private static bool ContainsMachine(FactoryRecord record, string id) => record.EntityId == id
        || (record.Data.TryGetProperty("sourceBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array
            && boxes.EnumerateArray().Any(b => b.GetProperty("entityId").GetString() == id));
}
