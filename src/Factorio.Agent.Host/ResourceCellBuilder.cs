using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds persistent drill cells on ore patches. Resumes an interrupted cell, continues an unfinished row, or plans a new
/// row on observed deposits, exploring toward remembered ones within a budget. The pole joins the network first, then
/// receivers precede their sources so no ore or plate spills; native drop and pickup targets prove every link. A resumed
/// cell first drops recorded parts that no longer stand; a cell whose proof fails, or whose attempts are spent, is
/// abandoned where it stands.
/// </summary>
public sealed class ResourceCellBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    public const int MaximumAttempts = 3;
    public const string Abandoned = "abandoned";
    private static readonly string[] BuildOrder = ["pole", "output-chest", "output-inserter", "furnace", "drill"];

    /// <summary>
    /// The interrupted cell of this product to resume, counting one more attempt, and the interrupted cells whose attempts
    /// are spent: those are abandoned where they stand and keep their slot.
    /// </summary>
    internal static (FactoryCell? Resume, IReadOnlyList<FactoryCell> Abandoned) Interrupted(FactoryState state, string kind, string product, string? cellId = null)
    {
        var interrupted = state.Cells.Where(c => c.IsResource && c.Status == "building" && c.Kind == kind && c.Recipe == product).ToArray();
        var resume = interrupted.FirstOrDefault(c => c.Attempts < MaximumAttempts && (cellId is null || c.Id == cellId));
        if (cellId is not null && resume is null) throw new InvalidOperationException("The selected resource cell can no longer be resumed.");
        return (resume is null ? null : resume with { Attempts = resume.Attempts + 1 },
            interrupted.Where(c => c.Attempts >= MaximumAttempts).Select(c => c with { Status = Abandoned }).ToArray());
    }

    public async Task<FactoryCell> BuildNextAsync(string product, double perMinute, CancellationToken token = default, int explorationBudget = 16,
        string? resumeCellId = null)
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
        var (cell, spent) = Interrupted(state, supply.Kind, product, resumeCellId);
        foreach (var worn in spent) await AbandonAsync(worn, "The cell spent its build attempts.");
        ResourceRow row;
        if (cell is not null)
        {
            row = state.Rows!.Single(r => r.Id == cell.Slot.Band);
        }
        else
        {
            int index;
            (row, index) = await NextSlotAsync();
            cell = new($"cell-{Guid.NewGuid():N}", 0, new(row.Id, index, true), row.Kind, row.Equipment.Drill, product,
                new Dictionary<string, string>(), "building", 0, Attempts: 1);
            await SaveAsync(cell);
        }
        string[] items = Items(row.Equipment);
        CellLayout layout = planner.Layout(await MapAsync(items, 8), row, cell.Slot.Index);
        cell = WithLayout(cell, layout);
        var zones = game is IDangerZoneReader reader
            ? await reader.ReadActiveDeathsAsync(catalog.Scope, 1, catalog.CollectedTick, token) : [];
        if (!Safe(cell, zones)) throw new InvalidOperationException("The resource cell waits for its recent death zone to clear.");
        await SaveAsync(cell);
        // CellLayout.Machine names assembler cells only; resource cells journal their parts explicitly.
        await journal.AppendAsync("resource-cell-plan", new { cell.Id, cell.Attempts, row, layout.Slot, layout.Entities, layout.Footprint, layout.Walkway }, token);
        if (cell.Entities.Count > 0) await ReconcileAsync();
        await new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(registry, catalog,
            CarriedStock.Unplaced(layout, cell.Entities), token);
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
            // A pole recorded before an interruption may still be unconnected. Link poles belong to the cell so maintenance
            // rebuilds them after an attack cuts the cell from the generators.
            if (planned.Role == "pole")
                await JoinNetworkAsync(ids["pole"], planned.Position, row.Equipment.Pole!, [.. items, .. (state.Rows ?? []).SelectMany(r => Items(r.Equipment))],
                    state, catalog, controller, async (linkId, link) =>
                    {
                        cell = FactoryCellBuilder.WithLink(cell!, linkId, link, row.Equipment.Pole!);
                        foreach (var (role, id) in cell.Entities) ids[role] = id;
                        await SaveAsync(cell);
                    }, token);
        }
        var built = await MapAsync(items, 32);
        // Native targets can resolve after the next entity update; observing again is not a mutation retry.
        for (int attempt = 0; Problem(built, ids) is { } problem; attempt++)
        {
            if (attempt >= 3)
            {
                // A disproven native link does not heal on resume: the cell is abandoned rather than retried forever.
                await AbandonAsync(cell, problem);
                throw new InvalidDataException(problem);
            }
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 300, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException($"Waiting for native targets ended with {waited.Status}.");
            built = await MapAsync(items, 32);
        }
        await new ResourceCellStartup(game, journal).StartAsync(cell, catalog, controller, token);
        built = await MapAsync(items, 32);
        if (Problem(built, ids) is { } changed)
            throw new InvalidDataException($"The resource cell changed while obtaining startup fuel: {changed}");
        cell = cell with { Status = "ready", Tick = built.CollectedTick };
        await SaveAsync(cell);
        await journal.AppendAsync("resource-cell-ready", cell, token);
        return cell;

        async Task<(ResourceRow Row, int Index)> NextSlotAsync()
        {
            foreach (var open in (state.Rows ?? []).Where(r => r.Product == product).OrderBy(r => r.Id))
            {
                var used = state.Cells.Where(c => c.IsResource && c.Slot.Band == open.Id).Select(c => c.Slot.Index).ToHashSet();
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
            var charting = new ChartedResourceSurvey(game, journal);
            var deferredResources = new HashSet<string>(StringComparer.Ordinal);
            for (int attempt = 0; ; attempt++)
            {
                var inventory = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
                var map = await MapAsync(CandidateItems(), 48);
                var candidates = ResourceCellPlanner.EquipmentCandidates(catalog, map, supply, inventory);
                if (candidates.Count == 0)
                    throw new InvalidOperationException($"No obtainable drill, receiver and power equipment supplies {product}.");
                var search = new ResourceRowSearch(ResourceRowSearchStatus.NoSite);
                for (int equipmentIndex = 0; equipmentIndex < candidates.Count; equipmentIndex++)
                {
                    var equipment = candidates[equipmentIndex];
                    if (ResourceCellPlanner.Observed(map, catalog, supply.Resource))
                    {
                        double rate = ResourceCellPlanner.CellPerMinute(map, catalog, supply, equipment);
                        int cells = (int)Math.Clamp(Math.Ceiling(perMinute / rate - 1e-9), 1, ResourceCellPlanner.MaximumRowCells);
                        var reserved = ResourceCellPlanner.Reserve(map, Reserved(map), equipment.Chest);
                        search = await ControllerPlanning.RunAsync(t => planner.Find(reserved, catalog, supply, equipment, cells, map.Actor.Position, t),
                            controller, TimeSpan.FromMinutes(2), token);
                    }
                    await journal.AppendAsync("resource-row-search", new { product, attempt, equipment, equipmentIndex, search.Status,
                        search.Row, clearance = search.Clearance?.Select(e => e.Id), map.CollectedTick }, token);
                    // Only a complete refusal justifies another equipment choice; budget exhaustion stays uncertain.
                    if (search.Status != ResourceRowSearchStatus.NoSite) break;
                }
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
                int deferred = DeferUnusableResources(search.Status, map, supply.Resource, deferredResources);
                if (deferred > 0)
                    await journal.AppendAsync("resource-row-search-deferred", new { product, supply.Resource, map.Scope, map.CollectedTick,
                        added = deferred, total = deferredResources.Count, lifetime = "current-row-search-only",
                        interpretation = "No row fits this complete local photograph; seek another deposit or frontier, not a claim that the resource is absent." }, token);
                await new SurvivalKitController(game, journal).BeforeTripAsync("resource-row-exploration", token);
                await charting.BeforeExplorationAsync(catalog, supply.Resource, "resource-row-exploration", token);
                var waypoint = await controller.FindExplorationWaypointAsync(exploration, catalog, supply.Resource, token: token,
                    deferredResourceIds: deferredResources);
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

        IEnumerable<WorldBox> Reserved(SpatialSnapshot map) => ReservedGround(state, map);

        // Recorded parts destroyed or replaced since they were built are built again. The factory photograph lists every
        // known own entity wherever the actor stands, so absence from it is native truth rather than a local view.
        async Task ReconcileAsync()
        {
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (known.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a resource cell; reconcile partial construction.");
            var standing = ResourceCellHealth.Standing(known, cell!.Entities, layout);
            if (standing.Count == cell.Entities.Count) return;
            await journal.AppendAsync("resource-cell-lost-parts", new { cell.Id, lost = cell.Entities.Where(p => !standing.ContainsKey(p.Key)) }, token);
            cell = cell with { Entities = standing };
            await SaveAsync(cell);
        }

        async Task AbandonAsync(FactoryCell value, string reason)
        {
            await SaveAsync(value with { Status = Abandoned });
            await journal.AppendAsync("resource-cell-abandoned", new { value.Id, value.Slot, value.Entities, value.Attempts, reason }, token);
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

        string? Problem(SpatialSnapshot map, IReadOnlyDictionary<string, string> built)
        {
            // Link poles may stand outside the cell's view; the network proof below covers them.
            var observed = built.Where(p => layout.Role(p.Key) is not null)
                .ToDictionary(p => p.Key, p => map.Entities.SingleOrDefault(e => e.Id == p.Value), StringComparer.Ordinal);
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

    /// <summary>Only a complete local NoSite defers its actual resource ids, for this one bounded row search.</summary>
    internal static int DeferUnusableResources(ResourceRowSearchStatus status, SpatialSnapshot map, string resource, ISet<string> deferred)
    {
        if (status != ResourceRowSearchStatus.NoSite || !map.Coverage.Atomic || !map.Coverage.Complete) return 0;
        int added = 0;
        foreach (var entity in map.Entities.Where(e => e.Name == resource && e.Amount > 0))
            if (deferred.Add(entity.Id)) added++;
        return added;
    }

    /// <summary>Recovers the full planned footprint of legacy cells, even when only power links were recorded.</summary>
    internal async Task<FactoryCell> PrepareResumeAsync(FactoryCell cell, FactoryState state, ActorScope scope, CancellationToken token)
    {
        var row = (state.Rows ?? []).SingleOrDefault(r => r.Id == cell.Slot.Band)
            ?? throw new InvalidDataException("The resource cell has no recorded row to recover its plan.");
        var map = await new SpatialClient(game).CaptureAsync(Items(row.Equipment), 8, token);
        if (map.Scope != scope) throw new InvalidDataException("Actor identity changed while recovering a resource cell plan.");
        var planned = WithLayout(cell, new ResourceCellPlanner().Layout(map, row, cell.Slot.Index));
        var registry = new FactoryRegistry(directory);
        await registry.SaveAsync((await registry.LoadAsync(scope.WorldId, token)).With(planned), token);
        return planned;
    }

    internal static FactoryCell WithLayout(FactoryCell cell, CellLayout layout) => cell with
    {
        Plan = layout.Entities.Concat((cell.Plan?.Values ?? []).Where(p => layout.Entities.All(e => e.Role != p.Role)))
            .ToDictionary(e => e.Role, StringComparer.Ordinal)
    };

    internal static bool Safe(FactoryCell cell, IReadOnlyList<NativeDeathTransition> zones) =>
        cell.Plan is { Count: > 0 } && cell.Plan.ContainsKey("drill") && cell.Plan.ContainsKey("output-chest")
        && cell.Plan.Values.All(p => !zones.Any(z => DangerZones.Covers(z, p.Position)));

    /// <summary>
    /// Reconnects ready resource cells whose pole stands on an island without a generator, registering the new link poles.
    /// On 2026-10-01 (seed 20261002) biters destroyed the unregistered links of the coal cells: the cells stayed ready on an
    /// unfed island and the boilers starved with them. Destroyed cell poles are left to maintenance and cell health.
    /// </summary>
    public async Task<int> RepairPowerAsync(CancellationToken token = default)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while repairing resource cell power.");
        string[] items = (state.Rows ?? []).SelectMany(r => Items(r.Equipment)).Distinct(StringComparer.Ordinal).ToArray();
        int repaired = 0;
        await using var controller = new SpatialController(game, journal);
        foreach (var cell in state.Cells.Where(c => c.IsResource && c.Status == "ready" && c.Entities.ContainsKey("pole")))
        {
            string poleId = cell.Entities["pole"];
            var pole = snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.EntityId == poleId);
            var row = state.Rows?.SingleOrDefault(r => r.Id == cell.Slot.Band);
            if (pole is null || row?.Equipment.Pole is not { } poleItem || FactoryPower.IsFed(snapshot, poleId) != false) continue;
            var position = pole.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            await controller.TravelAsync(position, 6, catalog, token);
            var repairedCell = cell;
            await JoinNetworkAsync(poleId, position, poleItem, items, state, catalog, controller, async (linkId, link) =>
            {
                repairedCell = FactoryCellBuilder.WithLink(repairedCell, linkId, link, poleItem);
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(repairedCell), token);
            }, token);
            // One link usually rejoins every cell of the same island.
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            repaired++;
        }
        if (repaired > 0) await journal.AppendAsync("resource-power-repair", new { repaired }, token);
        return repaired;
    }

    /// <summary>
    /// Extends the network to a cell pole until a generator shares its network, proven on the whole known factory: a pole
    /// that only touches other poles may sit on an island cut off by an attack. Only poles fed by a generator are sources.
    /// When the mod reports no network identities, joining any owned pole network is accepted as before.
    /// </summary>
    private async Task JoinNetworkAsync(string poleId, MapPosition polePosition, string poleItem, IReadOnlyList<string> items, FactoryState state,
        ProductionCatalog catalog, SpatialController controller, Func<string, PlacementCandidate, Task> registerLink, CancellationToken token)
    {
        var spatial = new SpatialClient(game);
        WorldBox? target = null;
        string? force = null;
        for (int link = 0; link < 64; link++)
        {
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var map = await spatial.CaptureAsync(items.Append(poleItem).Distinct(StringComparer.Ordinal).ToArray(), 48, token);
            if (known.Scope != catalog.Scope || map.Scope != catalog.Scope)
                throw new InvalidDataException("Actor identity changed while joining a resource cell to the network; reconcile partial construction.");
            bool? fed = FactoryPower.IsFed(known, poleId);
            if (fed == true) return;
            long? island = null;
            if (map.Entities.SingleOrDefault(e => e.Id == poleId) is { } pole)
            {
                (target, force, island) = (pole.Bounds, pole.Force, pole.Power?.NetworkId);
                if (fed is null && island is { } network && map.Entities.Any(e => e.Id != poleId && e.Power?.NetworkId == network
                    && (map.Prototypes[e.Name].Type == "electric-pole" || FactoryCellBuilder.IsPowerSource(map.Prototypes[e.Name].Type)))) return;
            }
            if (target is null)
            {
                // One approach tells a pole out of view from a destroyed one; a gone pole is never chased.
                if (link > 0 || map.Bounds.Contains(polePosition))
                    throw new InvalidOperationException($"The resource cell pole {poleId} is not observed at its planned position.");
                await controller.TravelAsync(polePosition, 6, catalog, token);
                continue;
            }
            bool Source(SpatialEntity e) => e.Force == force && e.Id != poleId
                && (fed is null ? island is null || e.Power?.NetworkId != island : FactoryPower.IsFed(known, e.Id) == true);
            var owned = map.Entities.Where(Source).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var reserved = ResourceCellPlanner.Reserve(map, ReservedGround(state, map), poleItem);
            var next = new PowerGridPlanner().Next(reserved, poleItem, target, owned, token);
            await journal.AppendAsync("resource-power-link", new { poleId, fed, next, map.CollectedTick }, token);
            if (next.Status == PowerGridSearchStatus.Extension && next.Pole is not null)
            {
                // Link poles come from the carried stock, for the whole planned chain at once.
                await new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(new FactoryRegistry(directory), catalog,
                    poleItem, PowerGridPlanner.ChainPoles(next), token);
                string added = await new PoweredMachineController(game, journal).BuildAtAsync(poleItem, next.Pole, catalog, controller, token);
                await registerLink(added, next.Pole);
                await journal.AppendAsync("resource-power-pole", new { poleId, added, next.Pole.Position }, token);
                await controller.TravelAsync(next.Pole.Position, 3, catalog, token);
                continue;
            }
            if (next.Status != PowerGridSearchStatus.NoObservedPath || map.Entities.Any(e => owned.Contains(e.Id)
                && map.Prototypes[e.Name].Type == "electric-pole" && e.Power?.NetworkId is not null))
                throw new InvalidOperationException($"The resource cell pole cannot join the observed network: {next.Status}.");
            // No fed network in view: walk to the nearest known fed pole and extend from there.
            var remote = known.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
                    && r.Data.GetProperty("type").GetString() == "electric-pole" && !map.Entities.Any(e => e.Id == r.EntityId)
                    && (fed is null || FactoryPower.IsFed(known, r.EntityId) == true))
                .Select(r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!)
                .OrderBy(p => p.DistanceTo(polePosition)).FirstOrDefault()
                ?? throw new InvalidOperationException("No owned electric network with a generator is known; build steam power first.");
            await controller.TravelAsync(remote, 6, catalog, token);
        }
        throw new InvalidOperationException("Joining the resource cell to the electric network exceeded its link budget.");
    }

    /// <summary>Every planned row and factory band keeps its ground, including cells not built yet and walkways.</summary>
    private static IEnumerable<WorldBox> ReservedGround(FactoryState state, SpatialSnapshot map) =>
        (state.Rows ?? []).SelectMany(r => ResourceCellPlanner.Reservation(map, r))
            .Concat(state.Zones.Select(z => new WorldBox(z.Origin, new(z.Origin.X + z.Slots * z.Pitch, z.Origin.Y + z.BandHeight))));

    private static string[] Items(ResourceCellEquipment equipment) =>
        new[] { equipment.Drill, equipment.Chest, equipment.Furnace, equipment.Inserter, equipment.Pole }.OfType<string>().ToArray();

    private static MapPosition Center(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);
}
