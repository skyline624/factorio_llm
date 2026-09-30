using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds persistent fluid cells outside factory bands: extractors on fluid deposits, and fluid machines with their pole,
/// chest-fed inserters when the recipe moves solids, C#-routed pipes and power links. Every id comes from a build receipt and
/// every role keeps its planned position, so maintenance can rebuild it; an interrupted cell resumes with what already stands.
/// An extractor feeds exactly one machine, so each new machine consuming an extracted fluid is paired with a free extractor.
/// </summary>
public sealed class FluidCellBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    public const string ExtractorKind = "extractor";
    public const string MachineKind = "fluid";
    // The machine is committed before the small parts whose approach must avoid its footprint.
    private static readonly string[] PartOrder = ["drill", "machine", "pole", "input-inserter", "input-chest", "output-inserter", "output-chest"];

    /// <summary>Builds one extractor cell on the nearest free observed or remembered deposit, powered and proven to hold its fluid.</summary>
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
        string[] items = [.. drills, template.Pole, pipeItem];
        await using var controller = new SpatialController(game, journal);
        var cell = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.FirstOrDefault(c =>
            c.Kind == ExtractorKind && c.Status == "building" && c.Recipe == product && c.Plan is not null);
        if (cell is null)
        {
            var (site, drillItem, tick) = await FindExtractorAsync(resource, template, pipeItem, items, catalog, controller, token);
            cell = new($"fluid-{Guid.NewGuid():N}", 0, new(0, 0, true), ExtractorKind, drillItem, product, new Dictionary<string, string>(),
                "building", tick, Plan: Roles(site.Layout, "drill"));
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-extractor-plan", new { cell.Id, resource, product, site, tick }, token);
        }
        var equipment = FactoryCellBuilder.Equipment(catalog, cell.MachineItem);
        cell = await PlaceAsync(cell, registry, catalog, controller, items, token);
        cell = await PowerAsync(cell, registry, catalog, equipment, controller, items, token);
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
        string[] items = [machineItem, equipment.Inserter, equipment.Chest, equipment.Pole, pipeItem, .. pumps];
        var registry = new FactoryRegistry(directory);
        await using var controller = new SpatialController(game, journal);
        var cell = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.FirstOrDefault(c => c.Kind == MachineKind
            && c.Status == "building" && c.MachineItem == machineItem && c.Recipe == recipeName && c.Plan is not null);
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
            // A terrain fluid no observed pump draws yet gets a new pump beside the machine, so the machine moves to that shore.
            string[] pumped = fluids.Where(f => FluidChainPlanner.Terrain(catalog, f) && !map.Entities.Any(e =>
                map.Prototypes[e.Name].Type == "offshore-pump" && OffshoreSupplyPlanner.CanExtract(map, e.Id, f))).ToArray();
            if (pumped.Length > 0)
            {
                var shore = Shore(map, pumped[0], anchor) ?? throw new InvalidOperationException($"No observed {pumped[0]} tile near the supply for a new pump.");
                if (shore.DistanceTo(anchor) > 8)
                {
                    anchor = shore;
                    await controller.TravelAsync(anchor, 6, catalog, token);
                    map = await CaptureAsync(items, catalog, token);
                }
            }
            var stock = await SnapshotAsync(catalog, token);
            string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
            FluidSupplyRoute? Route(SpatialSnapshot current, string fluid)
            {
                if (sources.GetValueOrDefault(fluid) is { } source)
                    return new PipeRoutePlanner().Find(current, pipeItem, source, FluidCellPlanner.PlannedId, fluid) is { Status: PipeRouteStatus.Found } route
                        ? new(source, route) : null;
                if (pumped.Contains(fluid))
                    return pumps.Select(pump => new OffshoreSupplyPlanner().Find(current, pump, pipeItem, FluidCellPlanner.PlannedId, fluid))
                        .FirstOrDefault(p => p is not null) is { } offshore ? new("planned:offshore-supply", offshore.Route) : null;
                return new FluidSupplyPlanner().Find(current, stock, pipeItem, FluidCellPlanner.PlannedId, fluid);
            }
            var site = await ControllerPlanning.RunAsync(t => new FluidCellPlanner().Find(map, force, equipment, pipeItem, anchor, input, output,
                    fluids, Route, cancellationToken: t), controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException($"No clear site near the {string.Join(", ", fluids)} supply routes every port assignment of {recipeName}.");
            cell = new($"fluid-{Guid.NewGuid():N}", 0, new(0, 0, true), MachineKind, machineItem, recipeName, new Dictionary<string, string>(),
                "building", map.CollectedTick, Plan: Roles(site.Layout, "machine"));
            await SaveAsync(registry, catalog, cell, token);
            await journal.AppendAsync("fluid-cell-plan", new { cell.Id, recipe = recipeName, sources, pumped, anchor, site, map.CollectedTick }, token);
        }
        cell = await PlaceAsync(cell, registry, catalog, controller, items, token);
        await ConfigureAsync(cell.Entities["machine"], cell.Plan!["machine"].Position, recipeName, catalog, controller, token);
        cell = await ConnectFluidsAsync(cell, fluids, registry, catalog, controller, pipeItem, token);
        cell = await PowerAsync(cell, registry, catalog, equipment, controller, items, token);
        var snapshot = await SnapshotAsync(catalog, token);
        if (FactoryPower.IsFed(snapshot, cell.Entities["machine"]) == false)
            throw new InvalidOperationException("The fluid machine's network has no power source.");
        return await ReadyAsync(registry, catalog, cell, token);
    }

    /// <summary>
    /// Native output rate of every ready extractor cell of a fluid, read from the deposit under its drill. Cells whose drill or
    /// deposit is not observed after travelling to it count as zero; at most sixteen cells are visited.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> ExtractorRatesAsync(string product, CancellationToken token = default)
    {
        var catalog = await CatalogAsync(token);
        var cells = (await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token)).Cells
            .Where(c => c.Kind == ExtractorKind && c.Status == "ready" && c.Recipe == product && c.Entities.ContainsKey("drill") && c.Plan?.ContainsKey("drill") == true)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Take(16).ToArray();
        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var controller = new SpatialController(game, journal);
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
        string[] items, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var map = await CaptureAsync(items, catalog, token);
            foreach (string item in items.Where(i => map.Items.TryGetValue(i, out var placeable)
                && map.Prototypes[placeable.EntityName] is { Type: "mining-drill", IsElectric: true } drill
                && drill.FluidBoxes?.Any(b => b.ProductionType == "output") == true
                && (carried.GetValueOrDefault(i) > 0 || FactoryDirector.Enabled(catalog, i))))
                if (new FluidCellPlanner().FindExtractor(map, template with { Machine = item }, resource, pipeItem) is { } site)
                    return (site, item, map.CollectedTick);
            // Remembered deposits only guide travel; geometry and amounts are observed again on arrival.
            var remembered = game is IResourceMemoryReader reader
                ? (await reader.ReadResourceMemoryAsync(map, token)).Resources.Where(r => r.Name == resource && !visited.Contains(r.EntityId)
                    && r.Position.DistanceTo(map.Actor.Position) > 24).OrderBy(r => r.Position.DistanceTo(map.Actor.Position)).FirstOrDefault()
                : null;
            await journal.AppendAsync("fluid-extractor-search", new { resource, attempt, remembered, map.CollectedTick }, token);
            if (remembered is null) break;
            visited.Add(remembered.EntityId);
            await controller.TravelAsync(remembered.Position, 8, catalog, token);
        }
        throw new InvalidOperationException($"No observed or remembered {resource} deposit offers a free extractor site; explore first.");
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

    /// <summary>Where the machine should stand: its paired extractor, else the nearest holder of a supplied fluid, else the actor.</summary>
    private async Task<MapPosition> AnchorAsync(IReadOnlyList<string> fluids, IReadOnlyDictionary<string, string?> sources,
        ProductionCatalog catalog, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(catalog, token);
        var actor = Position(snapshot, snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor").EntityId);
        foreach (string fluid in fluids)
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

    private async Task ConfigureAsync(string machineId, MapPosition position, string recipe, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        var state = await new ProductionController(game, journal).ObserveAsync(token);
        if (state.Entities.SingleOrDefault(e => e.Id == machineId)?.Recipe == recipe) return;
        await controller.ApproachEntityAsync(machineId, position, catalog, token);
        var configured = await controller.WorkAsync("set_recipe", new { entityId = machineId, recipe }, 600, token: token);
        if (configured.Status != "completed") throw new InvalidOperationException($"Fluid machine set_recipe ended with {configured.Status}: {configured.Error?.Code}.");
    }

    /// <summary>
    /// Pipes every fluid input still unconnected: extracted fluids from the paired extractor, the others jointly from the nearest
    /// stocks, with a new offshore pump for a terrain fluid nothing supplies yet. Built pipes and pumps become cell roles.
    /// </summary>
    private async Task<FactoryCell> ConnectFluidsAsync(FactoryCell cell, IReadOnlyList<string> fluids, FactoryRegistry registry,
        ProductionCatalog catalog, SpatialController controller, string pipeItem, CancellationToken token)
    {
        string machineId = cell.Entities["machine"];
        var machinePosition = cell.Plan!["machine"].Position;
        var map = await CaptureAsync([pipeItem], catalog, token);
        bool Connected(SpatialSnapshot current, string fluid) => current.Entities.Single(e => e.Id == machineId).FluidConnections?
            .Any(p => p.Filter == fluid && p.FlowDirection is "input" or "input-output" && p.TargetEntityId is not null) == true;
        var pending = fluids.Where(f => !Connected(map, f)).ToList();
        foreach (string fluid in pending.Where(f => FluidChainPlanner.Resource(catalog, f) is not null).ToArray())
        {
            if (await FreeExtractorAsync(fluid, machinePosition, catalog, controller, token) is not { } source) continue;
            cell = await RecordPipesAsync(cell, await new PipeConnectionController(game, journal).RunAsync(source, machineId, fluid, token),
                registry, catalog, pipeItem, token);
            pending.Remove(fluid);
        }
        if (pending.Count == 0) return cell;
        await controller.ApproachEntityAsync(machineId, machinePosition, catalog, token);
        map = await CaptureAsync([pipeItem], catalog, token);
        var stock = await SnapshotAsync(catalog, token);
        foreach (string terrain in pending.Where(f => FluidChainPlanner.Terrain(catalog, f)
            && new FluidSupplyPlanner().Find(map, stock, pipeItem, machineId, f, token) is null).ToArray())
        {
            string pump = await new OffshoreSupplyController(game, journal).PrepareJointSourceAsync(machineId, terrain, pending, catalog, controller, token)
                ?? throw new InvalidOperationException($"No observed shore supports a {terrain} pump with every route of the machine.");
            var built = (await CaptureAsync([pipeItem], catalog, token)).Entities.Single(e => e.Id == pump);
            string item = catalog.Items.Where(p => p.Value.PlaceEntity == built.Name).Select(p => p.Key).Order(StringComparer.Ordinal).First();
            cell = WithRole(cell, "pump", pump, new("pump", item, built.Position, built.Direction));
            await SaveAsync(registry, catalog, cell, token);
        }
        map = await CaptureAsync([pipeItem], catalog, token);
        stock = await SnapshotAsync(catalog, token);
        var routes = new MultiFluidSupplyPlanner().Find(map, stock, pipeItem, machineId, pending, token)
            ?? throw new InvalidOperationException($"The fluid machine has no joint route for {string.Join(", ", pending)}.");
        await journal.AppendAsync("fluid-cell-routes", new { cell.Id, machineId, routes, stock.CollectedTick }, token);
        foreach (var route in routes)
            cell = await RecordPipesAsync(cell, await new PipeConnectionController(game, journal).RunAsync(route.Supply.SourceId, machineId, route.Fluid, token),
                registry, catalog, pipeItem, token);
        return cell;
    }

    private async Task<FactoryCell> RecordPipesAsync(FactoryCell cell, PipeConnectionResult result, FactoryRegistry registry,
        ProductionCatalog catalog, string pipeItem, CancellationToken token)
    {
        if (result.BuiltPipeIds.Count == 0) return cell;
        var map = await CaptureAsync([pipeItem], catalog, token);
        int index = 0;
        foreach (var pipe in map.Entities.Where(e => result.BuiltPipeIds.Contains(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            while (cell.Entities.ContainsKey($"pipe-{index}")) index++;
            cell = WithRole(cell, $"pipe-{index}", pipe.Id, new($"pipe-{index}", pipeItem, pipe.Position, 0));
        }
        if (cell.Entities.Values.Intersect(result.BuiltPipeIds).Count() != result.BuiltPipeIds.Count)
            throw new InvalidDataException("A built pipe is not observed; reconcile the route before registering the cell.");
        await SaveAsync(registry, catalog, cell, token);
        await journal.AppendAsync("fluid-cell-pipes", new { cell.Id, result.SourceId, result.TargetId, result.Fluid, result.BuiltPipeIds }, token);
        return cell;
    }

    private async Task<FactoryCell> PlaceAsync(FactoryCell cell, FactoryRegistry registry, ProductionCatalog catalog, SpatialController controller,
        string[] items, CancellationToken token)
    {
        var plan = cell.Plan!;
        string[] parts = PartOrder.Where(plan.ContainsKey).ToArray();
        var builder = new FactoryCellBuilder(game, journal, directory);
        // Every missing part is carried first, so no production trip interrupts construction.
        foreach (var group in parts.Where(r => !cell.Entities.ContainsKey(r)).GroupBy(r => plan[r].Item))
            await builder.EnsureCarriedAsync(registry, catalog, group.Key, group.Count(), token);
        for (int index = 0; index < parts.Length; index++)
        {
            if (cell.Entities.ContainsKey(parts[index])) continue;
            var planned = plan[parts[index]];
            var remaining = parts.Skip(index + 1).Where(r => !cell.Entities.ContainsKey(r)).Select(r => plan[r].Position).ToArray();
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

    private async Task<FactoryCell> PowerAsync(FactoryCell cell, FactoryRegistry registry, ProductionCatalog catalog, CellEquipment equipment,
        SpatialController controller, string[] items, CancellationToken token)
    {
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        var zones = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Zones;
        var context = new FactoryCellBuilder.BuildContext(catalog, equipment, null, controller, registry, items, map => steam is null ? map
            : PowerExpansionController.ReserveGrowth(map, steam, zones, map.Entities.Single(e => e.Id == map.Actor.Id).Force));
        await new FactoryCellBuilder(game, journal, directory).ConnectPowerAsync(context, cell.Entities["pole"], cell.Plan!["pole"].Position, token,
            async (linkId, link) =>
            {
                // The link belongs to this cell so maintenance rebuilds it when an attack cuts the cell off.
                cell = FactoryCellBuilder.WithLink(cell, linkId, link, equipment.Pole);
                await SaveAsync(registry, catalog, cell, token);
            });
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

    /// <summary>Cell plan by role; the layout's machine takes the given role (miners and extractors call it drill).</summary>
    internal static IReadOnlyDictionary<string, PlannedEntity> Roles(CellLayout layout, string machineRole) => layout.Entities
        .Select(e => e.Role == "machine" ? e with { Role = machineRole } : e).ToDictionary(e => e.Role, StringComparer.Ordinal);

    /// <summary>The observed tile of a terrain fluid nearest to a point.</summary>
    internal static MapPosition? Shore(SpatialSnapshot map, string fluid, MapPosition near) => map.Rows
        .Where(r => map.TileFluids?.GetValueOrDefault(r.Name) == fluid)
        .SelectMany(r => Enumerable.Range(r.X, r.Length).Select(x => new MapPosition(x + .5, r.Y + .5)))
        .OrderBy(p => p.DistanceTo(near)).ThenBy(p => p.Y).ThenBy(p => p.X).FirstOrDefault();

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
        var map = await new SpatialClient(game).CaptureAsync(items, 48, token);
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
