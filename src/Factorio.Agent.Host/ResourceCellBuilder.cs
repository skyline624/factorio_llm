using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds persistent drill cells on ore patches. Resumes an interrupted cell, continues an unfinished row, or plans a new
/// row on observed deposits, exploring toward remembered ones within a budget. The pole joins the network first, then
/// receivers precede their sources so no ore or plate spills; native drop and pickup targets prove every link.
/// </summary>
public sealed class ResourceCellBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    private static readonly string[] BuildOrder = ["pole", "output-chest", "output-inserter", "furnace", "drill"];

    public async Task<FactoryCell> BuildNextAsync(string product, double perMinute, CancellationToken token = default, int explorationBudget = 16)
    {
        if (!double.IsFinite(perMinute) || perMinute <= 0 || perMinute > 10000) throw new ArgumentOutOfRangeException(nameof(perMinute));
        if (explorationBudget is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(explorationBudget));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var supply = ResourceCellPlanner.Supply(catalog, product)
            ?? throw new InvalidOperationException($"No miner or smelter cell can supply {product}.");
        var registry = new FactoryRegistry(directory);
        var planner = new ResourceCellPlanner();
        var spatial = new SpatialClient(game);
        await using var controller = new SpatialController(game, journal);

        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        FactoryCell? cell = state.Cells.FirstOrDefault(c => c.Zone == 0 && c.Status == "building" && c.Kind == supply.Kind && c.Recipe == product);
        ResourceRow row;
        if (cell is not null) row = state.Rows!.Single(r => r.Id == cell.Slot.Band);
        else
        {
            int index;
            (row, index) = await NextSlotAsync();
            cell = new($"cell-{Guid.NewGuid():N}", 0, new(row.Id, index, true), row.Kind, row.Equipment.Drill, product,
                new Dictionary<string, string>(), "building", 0);
            await SaveAsync(cell);
        }
        string[] items = Items(row.Equipment);
        CellLayout layout = planner.Layout(await MapAsync(items, 8), row, cell.Slot.Index);
        // CellLayout.Machine names assembler cells only; resource cells journal their parts explicitly.
        await journal.AppendAsync("resource-cell-plan", new { cell.Id, row, layout.Slot, layout.Entities, layout.Footprint, layout.Walkway }, token);
        await EnsureItemsAsync(layout.Entities.GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
        await controller.TravelAsync(Center(layout.Walkway), 1, catalog, token);
        await ClearAsync();

        var ids = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        foreach (var planned in layout.Entities.OrderBy(e => Array.IndexOf(BuildOrder, e.Role)))
        {
            if (!ids.ContainsKey(planned.Role))
            {
                var remaining = layout.Entities.Where(e => e != planned && !ids.ContainsKey(e.Role)).Select(e => e.Position).ToArray();
                ids[planned.Role] = await PlaceAsync(planned, remaining);
                cell = cell with { Entities = new Dictionary<string, string>(ids, StringComparer.Ordinal) };
                await SaveAsync(cell);
            }
            // A pole recorded before an interruption may still be unconnected.
            if (planned.Role == "pole") await ConnectPowerAsync(ids["pole"], planned.Position);
        }
        var built = await MapAsync(items, 32);
        // Native targets can resolve after the next entity update; observing again is not a mutation retry.
        for (int attempt = 0; Problem(built, ids) is { } problem; attempt++)
        {
            if (attempt >= 3) throw new InvalidDataException(problem);
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 300, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException($"Waiting for native targets ended with {waited.Status}.");
            built = await MapAsync(items, 32);
        }
        cell = cell with { Status = "ready", Tick = built.CollectedTick };
        await SaveAsync(cell);
        await journal.AppendAsync("resource-cell-ready", cell, token);
        return cell;

        async Task<(ResourceRow Row, int Index)> NextSlotAsync()
        {
            foreach (var open in (state.Rows ?? []).Where(r => r.Product == product).OrderBy(r => r.Id))
            {
                var used = state.Cells.Where(c => c.Zone == 0 && c.Slot.Band == open.Id).Select(c => c.Slot.Index).ToHashSet();
                int next = Enumerable.Range(0, open.Cells).FirstOrDefault(i => !used.Contains(i), -1);
                if (next < 0) continue;
                string[] openItems = Items(open.Equipment);
                await controller.TravelAsync(Center(planner.Layout(await MapAsync(openItems, 8), open, next).Walkway), 1, catalog, token);
                if (planner.Fits(await MapAsync(openItems, 48), catalog, open, next)) return (open, next);
                // The ground changed since planning: keep the built part of the row and plan elsewhere.
                var shortened = open with { Cells = next };
                state = (await registry.LoadAsync(catalog.Scope.WorldId, token)).With(shortened);
                await registry.SaveAsync(state, token);
                await journal.AppendAsync("resource-row-shortened", new { open, shortened.Cells }, token);
            }
            return (await PlanRowAsync(), 0);
        }

        async Task<ResourceRow> PlanRowAsync()
        {
            var exploration = new ExplorationPlanner();
            for (int attempt = 0; ; attempt++)
            {
                var inventory = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
                var map = await MapAsync(CandidateItems(), 48);
                var equipment = ResourceCellPlanner.Equipment(catalog, map, supply, inventory)
                    ?? throw new InvalidOperationException($"No obtainable drill, receiver and power equipment supplies {product}.");
                var search = new ResourceRowSearch(ResourceRowSearchStatus.NoSite);
                if (ResourceCellPlanner.Observed(map, catalog, supply.Resource))
                {
                    double rate = ResourceCellPlanner.CellPerMinute(map, catalog, supply, equipment);
                    int cells = (int)Math.Clamp(Math.Ceiling(perMinute / rate - 1e-9), 1, ResourceCellPlanner.MaximumRowCells);
                    var reserved = ResourceCellPlanner.Reserve(map, Reserved(map), equipment.Chest);
                    search = await ControllerPlanning.RunAsync(t => planner.Find(reserved, catalog, supply, equipment, cells, map.Actor.Position, t),
                        controller, TimeSpan.FromMinutes(2), token);
                }
                await journal.AppendAsync("resource-row-search", new { product, attempt, equipment, search.Status, search.Row,
                    clearance = search.Clearance?.Select(e => e.Id), map.CollectedTick }, token);
                if (search.Status == ResourceRowSearchStatus.Found)
                {
                    state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                    var planned = search.Row! with { Id = (state.Rows ?? []).Select(r => r.Id).DefaultIfEmpty(0).Max() + 1 };
                    state = state.With(planned);
                    await registry.SaveAsync(state, token);
                    await journal.AppendAsync("resource-row", planned, token);
                    return planned;
                }
                if (attempt >= explorationBudget)
                    throw new TimeoutException($"No observed {supply.Resource} deposit holds a resource row within the exploration budget ({search.Status}).");
                var waypoint = await controller.FindExplorationWaypointAsync(exploration, catalog, supply.Resource, token: token);
                await journal.AppendAsync("resource-row-exploration", new { product, supply.Resource, waypoint }, token);
                await controller.NavigateAsync(waypoint.Position, cancellationToken: token);
            }
        }

        string[] CandidateItems()
        {
            var candidates = catalog.Items.Where(p => p.Value.PlaceEntity is not null && (p.Value.PlaceEntityType == "mining-drill"
                    || supply.Recipe is not null && catalog.Machines.ContainsKey(p.Key)
                    || p.Key is "iron-chest" or "wooden-chest" or "inserter" or "small-electric-pole"))
                .Select(p => p.Key).Concat((state.Rows ?? []).SelectMany(r => Items(r.Equipment)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return candidates.Length <= 16 ? candidates : throw new InvalidOperationException("Too many candidate cell items for one native observation.");
        }

        // Every planned row and factory band keeps its ground, including cells not built yet and walkways.
        IEnumerable<WorldBox> Reserved(SpatialSnapshot map) => (state.Rows ?? []).SelectMany(r => ResourceCellPlanner.Reservation(map, r))
            .Concat(state.Zones.Select(z => new WorldBox(z.Origin, new(z.Origin.X + z.Slots * z.Pitch, z.Origin.Y + z.BandHeight))));

        async Task EnsureItemsAsync(IReadOnlyDictionary<string, int> needed)
        {
            var production = new ProductionController(game, journal);
            var executor = new ProductionGoalExecutor(game, journal);
            foreach (var (item, count) in needed)
            {
                int missing = count - layout.Entities.Count(e => e.Item == item && cell!.Entities.ContainsKey(e.Role));
                if (missing <= 0) continue;
                var carried = (await production.ObserveAsync(token)).Inventory.GetValueOrDefault(item);
                if (carried < missing) await executor.RunAsync(item, Math.Min(1000, missing), token);
            }
        }

        async Task ClearAsync()
        {
            var walkway = layout.Walkway;
            var area = new WorldBox(new(Math.Min(layout.Footprint.Min.X, walkway.Min.X - 1), Math.Min(layout.Footprint.Min.Y, walkway.Min.Y - 1)),
                new(Math.Max(layout.Footprint.Max.X, walkway.Max.X + 1), Math.Max(layout.Footprint.Max.Y, walkway.Max.Y + 1)));
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var map = await MapAsync(items, 32);
                var obstacle = map.Entities.Where(e => FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type) && e.Bounds.Overlaps(area))
                    .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).FirstOrDefault();
                if (obstacle is null) return;
                await controller.TravelAsync(obstacle.Position, 2, catalog, token);
                var mined = await controller.WorkAsync("mine", new { name = obstacle.Name, position = obstacle.Position, count = 1 }, 1800, token: token);
                if (mined.Status is not ("completed" or "partial")) throw new InvalidOperationException($"Clearing {obstacle.Name} ended with {mined.Status}: {mined.Error?.Code}.");
                await journal.AppendAsync("resource-clearance", new { obstacle.Id, obstacle.Name, obstacle.Position }, token);
            }
            throw new InvalidOperationException("Resource cell clearance exceeded its obstacle budget.");
        }

        async Task<string> PlaceAsync(PlannedEntity planned, IReadOnlyList<MapPosition> remaining)
        {
            var map = await MapAsync(items, 32);
            string entityName = map.Items[planned.Item].EntityName;
            var existing = map.Entities.FirstOrDefault(e => e.Name == entityName && e.Position.DistanceTo(planned.Position) < .01
                && (map.Prototypes[entityName].Type is "container" or "electric-pole" or "furnace" || e.Direction == planned.Direction));
            if (existing is not null) return existing.Id; // Built before an interruption; the receipt was already applied.
            return await new PoweredMachineController(game, journal).BuildAtAsync(planned.Item, new(planned.Position, planned.Direction, 0),
                catalog, controller, token, remaining);
        }

        async Task ConnectPowerAsync(string poleId, MapPosition polePosition)
        {
            string poleItem = row.Equipment.Pole!;
            WorldBox? target = null;
            string? force = null;
            long? island = null;
            for (int link = 0; link < 64; link++)
            {
                var map = await MapAsync([.. items, .. (state.Rows ?? []).SelectMany(r => Items(r.Equipment))], 48);
                if (map.Entities.SingleOrDefault(e => e.Id == poleId) is { } pole)
                {
                    (target, force, island) = (pole.Bounds, pole.Force, pole.Power?.NetworkId);
                    if (island is { } network && map.Entities.Any(e => e.Id != poleId && e.Power?.NetworkId == network
                        && (map.Prototypes[e.Name].Type == "electric-pole" || FactoryCellBuilder.IsPowerSource(map.Prototypes[e.Name].Type)))) return;
                }
                if (target is null)
                {
                    await controller.TravelAsync(polePosition, 6, catalog, token);
                    continue;
                }
                // Poles of the unpowered island around this cell are not sources.
                var owned = map.Entities.Where(e => e.Force == force && e.Id != poleId && (island is null || e.Power?.NetworkId != island))
                    .Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                var reserved = ResourceCellPlanner.Reserve(map, Reserved(map), poleItem);
                var next = new PowerGridPlanner().Next(reserved, poleItem, target, owned, token);
                await journal.AppendAsync("resource-power-link", new { poleId, next, map.CollectedTick }, token);
                if (next.Status == PowerGridSearchStatus.Extension && next.Pole is not null)
                {
                    await EnsureItemsAsync(new Dictionary<string, int>(StringComparer.Ordinal) { [poleItem] = 1 });
                    string added = await new PoweredMachineController(game, journal).BuildAtAsync(poleItem, next.Pole, catalog, controller, token);
                    await journal.AppendAsync("resource-power-pole", new { poleId, added, next.Pole.Position }, token);
                    await controller.TravelAsync(next.Pole.Position, 3, catalog, token);
                    continue;
                }
                if (next.Status != PowerGridSearchStatus.NoObservedPath || map.Entities.Any(e => owned.Contains(e.Id)
                    && map.Prototypes[e.Name].Type == "electric-pole" && e.Power?.NetworkId is not null))
                    throw new InvalidOperationException($"The resource cell pole cannot join the observed network: {next.Status}.");
                // No owned network in view: walk to the nearest known pole and extend from there.
                var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                var remote = known.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
                        && r.Data.GetProperty("type").GetString() == "electric-pole" && !map.Entities.Any(e => e.Id == r.EntityId))
                    .Select(r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!)
                    .OrderBy(p => p.DistanceTo(polePosition)).FirstOrDefault()
                    ?? throw new InvalidOperationException("No owned electric network is known; build steam power first.");
                await controller.TravelAsync(remote, 6, catalog, token);
            }
            throw new InvalidOperationException("Joining the resource cell to the electric network exceeded its link budget.");
        }

        string? Problem(SpatialSnapshot map, IReadOnlyDictionary<string, string> built)
        {
            var observed = built.ToDictionary(p => p.Key, p => map.Entities.SingleOrDefault(e => e.Id == p.Value), StringComparer.Ordinal);
            if (observed.FirstOrDefault(p => p.Value is null) is { Key: { } missing }) return $"The built {missing} is not observed at its resource cell.";
            var drill = observed["drill"]!;
            if (!Drops(drill.DropPosition, drill.DropTargetId, observed[row.Equipment.Furnace is null ? "output-chest" : "furnace"]!))
                return "The drill does not drop into its planned receiver.";
            var arm = observed.GetValueOrDefault("output-inserter");
            var furnace = observed.GetValueOrDefault("furnace");
            if (arm is not null && (arm.PickupPosition is null || !(arm.PickupTargetId == furnace!.Id
                    || arm.PickupTargetId is null && furnace.Bounds.Contains(arm.PickupPosition))
                || !Drops(arm.DropPosition, arm.DropTargetId, observed["output-chest"]!)))
                return "The output inserter does not move from the furnace into the chest.";
            long? network = observed.GetValueOrDefault("pole")?.Power?.NetworkId;
            return new[] { drill, arm }.OfType<SpatialEntity>().Any(e => map.Prototypes[e.Name].IsElectric
                && (network is null || e.Power?.NetworkId != network)) ? "A resource cell consumer is not on its pole's electric network." : null;
        }

        // An unresolved native target (an unfuelled burner drill has not updated yet) is proven by the drop tile, as for installed extraction.
        static bool Drops(MapPosition? drop, string? target, SpatialEntity receiver) => drop is not null
            && (target == receiver.Id || target is null && ExtractionPlanner.DropTile(drop).Overlaps(receiver.Bounds));

        async Task SaveAsync(FactoryCell value) => await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(value), token);

        async Task<SpatialSnapshot> MapAsync(IReadOnlyList<string> requested, int radius)
        {
            var map = await spatial.CaptureAsync(requested.Distinct(StringComparer.Ordinal).ToArray(), radius, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a resource cell; reconcile partial construction.");
            return map;
        }
    }

    private static string[] Items(ResourceCellEquipment equipment) =>
        new[] { equipment.Drill, equipment.Chest, equipment.Furnace, equipment.Inserter, equipment.Pole }.OfType<string>().ToArray();

    private static MapPosition Center(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);
}
