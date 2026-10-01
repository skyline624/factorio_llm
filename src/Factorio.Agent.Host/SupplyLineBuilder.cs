using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds, resumes or extends the persistent supply line of one resource row: the collector along the row's walkway, the trunk routed
/// in observed segments to a depot beside the nearest band walkway, the depot chest and its inserter, then one feeder per ready cell.
/// Feeders come last, so no plate leaves a row chest before its path to the depot stands, powered and natively verified; the line is
/// ready from its first verified feeder. Every part is registered with its plan, so maintenance rebuilds destroyed parts in place,
/// and power joins only networks with a generator through the cell link machinery. An interrupted line resumes from its last built
/// belt; after its attempts it is abandoned where it stands and its row stays collected directly.
/// </summary>
public sealed class SupplyLineBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    public const int MaximumAttempts = 3;
    /// <summary>Observed trunk segments per build; each ends near the edge of one native observation.</summary>
    public const int MaximumSegments = 16;
    /// <summary>Trees and rocks one trunk segment may clear when no route avoids them.</summary>
    public const int MaximumClearance = 32;
    public const string Abandoned = "abandoned";
    public const string BeltItem = "transport-belt";

    public async Task<FactoryCell> BuildAsync(int rowId, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        await using var run = await Run.StartAsync(game, journal, directory, deadline.Token);
        return await run.BuildAsync(rowId);
    }

    /// <summary>
    /// Joins again the inserters of ready lines that no generator feeds, e.g. after an attack cut a link pole; new link poles are
    /// registered with the line. A line that cannot be reconnected is journaled; logistics collects its row directly while its
    /// inserters stay unpowered. A changed actor identity still stops the caller. Returns the inserters reconnected.
    /// </summary>
    public async Task<int> RepairPowerAsync(CancellationToken token = default)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        if (!state.Cells.Any(c => c.Kind == SupplyLinePlanner.Kind && c.Status == "ready")) return 0;
        int repaired = 0;
        try
        {
            await using var run = await Run.StartAsync(game, journal, directory, token);
            repaired = await run.RepairPowerAsync();
        }
        catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
        {
            if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != catalog.Scope) throw;
            await journal.AppendAsync("supply-line-power-repair-failed", new { error = error.GetType().Name, error.Message }, token);
        }
        if (repaired > 0) await journal.AppendAsync("supply-line-power-repair", new { repaired }, token);
        return repaired;
    }

    /// <summary>Line equipment: ordinary belts and the best craftable cell inserter, chest and pole.</summary>
    internal static SupplyLineEquipment Equipment(ProductionCatalog catalog)
    {
        if (!catalog.Items.ContainsKey(BeltItem)) throw new InvalidOperationException($"The catalog has no {BeltItem}.");
        var cell = FactoryCellBuilder.Equipment(catalog, BeltItem);
        return new(BeltItem, cell.Inserter, cell.Chest, cell.Pole);
    }

    /// <summary>The context of one build: catalog, registry view, equipment, captured items and the line being built.</summary>
    private sealed class Run(IGameClient game, IControllerJournal journal, string directory, CancellationToken token, ProductionCatalog catalog,
        FactoryState state, SupplyLineEquipment equipment, PowerExpansionController.SteamItems? steam, string[] items, EntityGeometry arm,
        SpatialController controller) : IAsyncDisposable
    {
        private readonly FactoryRegistry registry = new(directory);
        private readonly SpatialClient spatial = new(game);
        private FactoryCell line = null!;
        private ResourceRow row = null!;

        /// <summary>Every line records its plan from its start: maintenance rebuilds its parts from it.</summary>
        private IReadOnlyDictionary<string, PlannedEntity> Plan => line.Plan ?? throw new InvalidDataException($"Supply line {line.Id} has no plan.");

        public static async Task<Run> StartAsync(IGameClient game, IControllerJournal journal, string directory, CancellationToken token)
        {
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
            var equipment = Equipment(catalog);
            var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
            string[] items = new[] { equipment.Belt, equipment.Inserter, equipment.Chest, equipment.Pole }
                .Concat((state.Rows ?? []).SelectMany(r => new[] { r.Equipment.Drill, r.Equipment.Chest, r.Equipment.Furnace, r.Equipment.Inserter, r.Equipment.Pole }.OfType<string>()))
                .Concat(steam?.Machines ?? []).Distinct(StringComparer.Ordinal).ToArray();
            if (items.Length > 16) throw new InvalidOperationException("Too many row and line items for one native observation.");
            var prototypes = await new SpatialClient(game).CaptureAsync(items, 4, token);
            if (prototypes.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while preparing a supply line.");
            var arm = prototypes.Prototypes[prototypes.Items[equipment.Inserter].EntityName];
            return new(game, journal, directory, token, catalog, state, equipment, steam, items, arm, new SpatialController(game, journal));
        }

        public ValueTask DisposeAsync() => controller.DisposeAsync();

        public async Task<FactoryCell> BuildAsync(int rowId)
        {
            row = state.Rows?.SingleOrDefault(r => r.Id == rowId) ?? throw new InvalidOperationException($"Resource row {rowId} is not registered.");
            var access = ResourceCellPlanner.Access(await MapAsync(4), row);
            await StartLineAsync();
            if (line.Status == "ready")
            {
                // A ready line only gains feeders for cells that became ready since; it keeps serving meanwhile.
                int before = SupplyLines.FeederCells(line.Entities.Keys).Count;
                await FeedersAsync();
                await journal.AppendAsync("supply-line-extended", new { line.Id, row = row.Id, before, feeders = SupplyLines.FeederCells(line.Entities.Keys) }, token);
                return line;
            }
            await ReconcileAsync();
            if (!Plan.ContainsKey(SupplyLinePlanner.DepotChestRole)) await PlanDepotAsync(access);
            if (SupplyLines.FlowRoles(Plan.Keys).Count == 0) await PlanCollectorAsync(access);
            await FlowAsync();
            await TrunkAsync();
            await DepotAsync();
            await FeedersAsync();
            return line.Status == "ready" ? line : throw new InvalidOperationException($"No feeder of a ready cell of row {row.Id} could be verified.");
        }

        public async Task<int> RepairPowerAsync()
        {
            int repaired = 0;
            foreach (var ready in state.Cells.Where(c => c.Kind == SupplyLinePlanner.Kind && c.Status == "ready").ToArray())
            {
                line = ready;
                row = state.Rows?.SingleOrDefault(r => r.Id == ready.Slot.Band) ?? throw new InvalidDataException($"Supply line {ready.Id} lost its row.");
                foreach (string role in ready.Entities.Keys.Where(r => r == SupplyLinePlanner.DepotInserterRole || SupplyLines.FeederCells([r]).Count == 1).ToArray())
                {
                    var known = await SnapshotAsync();
                    if (FactoryMaintenance.Unpowered(known, [line.Entities[role]]).Count == 0) continue;
                    await PowerAsync(role);
                    repaired++;
                }
            }
            return repaired;
        }

        /// <summary>The row's line, new or resumed with one more attempt; a line that spent its attempts is abandoned where it stands.</summary>
        private async Task StartLineAsync()
        {
            var existing = SupplyLines.Line(await LoadAsync(), row.Id);
            if (existing is { Status: Abandoned }) throw new InvalidOperationException($"The supply line of row {row.Id} was abandoned.");
            if (existing is { Status: "building", Attempts: >= MaximumAttempts })
            {
                line = existing with { Status = Abandoned };
                await SaveAsync();
                await journal.AppendAsync("supply-line-abandoned", new { line.Id, row = row.Id, line.Attempts, line.Entities }, token);
                throw new InvalidOperationException($"The supply line of row {row.Id} spent its {MaximumAttempts} build attempts.");
            }
            line = existing switch
            {
                null => new($"supply-{Guid.NewGuid():N}", 0, new(row.Id, 0, true), SupplyLinePlanner.Kind, equipment.Belt, row.Product,
                    new Dictionary<string, string>(StringComparer.Ordinal), "building", 0, Attempts: 1,
                    Plan: new Dictionary<string, PlannedEntity>(StringComparer.Ordinal)),
                { Status: "building" } => existing with { Attempts = existing.Attempts + 1 },
                _ => existing
            };
            await SaveAsync();
            await journal.AppendAsync("supply-line-start", new { line.Id, row = row.Id, row.Product, line.Status, line.Attempts }, token);
        }

        /// <summary>
        /// Parts destroyed since they were recorded are built again in flow order; trunk plans past the last standing trunk belt are
        /// dropped, so the trunk is routed again from there, and lost link poles are left to a fresh link.
        /// </summary>
        private async Task ReconcileAsync()
        {
            var present = FactoryMaintenance.Present(await SnapshotAsync());
            var standing = line.Entities.Where(e => present.Contains(e.Value)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var trunk = SupplyLines.FlowRoles(Plan.Keys).Where(SupplyLines.IsTrunk).ToArray();
            int lastBuilt = Array.FindLastIndex(trunk, standing.ContainsKey);
            var plan = Plan.Where(p => (!SupplyLines.IsTrunk(p.Key) || Array.IndexOf(trunk, p.Key) <= lastBuilt)
                    && (!p.Key.StartsWith("link-", StringComparison.Ordinal) || standing.ContainsKey(p.Key)))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (standing.Count == line.Entities.Count && plan.Count == Plan.Count) return;
            await journal.AppendAsync("supply-line-lost-parts", new { line.Id, lost = line.Entities.Where(e => !standing.ContainsKey(e.Key)),
                dropped = Plan.Keys.Where(k => !plan.ContainsKey(k)) }, token);
            line = line with { Entities = standing, Plan = plan };
            await SaveAsync();
        }

        /// <summary>The depot beside the walkway end, among all bands, nearest the row; planned on observed ground with reservations.</summary>
        private async Task PlanDepotAsync(ResourceRowAccess access)
        {
            var rowCentre = Centre(access.Walkway);
            var zone = state.Zones.OrderBy(z => SupplyLinePlanner.End(Walkway(z), rowCentre).DistanceTo(rowCentre)).ThenBy(z => z.Id).First();
            var walkway = Walkway(zone);
            await controller.TravelAsync(SupplyLinePlanner.End(walkway, rowCentre), 2, catalog, token);
            var map = await MapAsync(48);
            var known = await SnapshotAsync();
            var fed = map.Entities.Where(e => FactoryPower.IsFed(known, e.Id) == true).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var depot = SupplyLinePlanner.Depot(Protect(map, null), equipment, walkway, rowCentre, fed)
                ?? throw new InvalidOperationException($"No depot site within {SupplyLinePlanner.DepotRadius} tiles of band {zone.Id}'s walkway end.");
            line = line with { Plan = With(Plan, depot.Chest, depot.Inserter) };
            await SaveAsync();
            await journal.AppendAsync("supply-line-depot", new { line.Id, zone = zone.Id, walkway, depot, map.CollectedTick }, token);
        }

        /// <summary>Feeders and the collector along the row's walkway, flowing to the end nearer the depot's pickup tile.</summary>
        private async Task PlanCollectorAsync(ResourceRowAccess access)
        {
            await controller.TravelAsync(Centre(access.Walkway), 2, catalog, token);
            var map = await MapAsync(48);
            var collector = SupplyLinePlanner.Collector(Protect(map, row), row, equipment, Pickup())
                ?? throw new InvalidOperationException($"Row {row.Id} has no free collector along its walkway.");
            var ready = await ReadyCellsAsync();
            if (!collector.Feeders.Any(f => ready.ContainsKey(f.Cell)))
                throw new InvalidOperationException($"No ready cell of row {row.Id} has a free feeder tile.");
            line = line with { Plan = With(Plan, [.. collector.Belts, .. collector.Feeders.Select(f => f.Inserter)]) };
            await SaveAsync();
            await journal.AppendAsync("supply-line-collector", new { line.Id, row = row.Id, collector, map.CollectedTick }, token);
        }

        /// <summary>Builds the planned belts that do not stand yet, in flow order, proving each native connection as it forms.</summary>
        private async Task FlowAsync()
        {
            var roles = SupplyLines.FlowRoles(Plan.Keys);
            int missing = roles.Count(r => !line.Entities.ContainsKey(r));
            if (missing == 0) return;
            await EnsureCarriedAsync(equipment.Belt, missing);
            for (int index = 0; index < roles.Count; index++)
            {
                if (line.Entities.ContainsKey(roles[index])) continue;
                string id = await PlaceAsync(roles[index]);
                await VerifyLinkAsync(index > 0 ? line.Entities[roles[index - 1]] : null, id,
                    index + 1 < roles.Count ? line.Entities.GetValueOrDefault(roles[index + 1]) : null);
            }
        }

        /// <summary>
        /// Routes the trunk from the belt after the last standing one to the depot's pickup tile, one observed segment at a time. A
        /// segment is planned whole before its belts are built, so an interrupted build knows it; removable obstacles are cleared only
        /// when no route avoids them.
        /// </summary>
        private async Task TrunkAsync()
        {
            MapPosition pickup = Pickup();
            for (int segment = 0; ; segment++)
            {
                var roles = SupplyLines.FlowRoles(Plan.Keys);
                var last = Plan[roles[^1]];
                if (last.Position == pickup) return;
                if (segment >= MaximumSegments) throw new InvalidOperationException($"The trunk of row {row.Id} exceeded {MaximumSegments} observed segments.");
                int trunk = roles.Count(SupplyLines.IsTrunk);
                var head = BeltRoutePlanner.Ahead(last.Position, last.Direction);
                await controller.TravelAsync(head, 2, catalog, token);
                var map = Protect(await MapAsync(48), null);
                string joined = line.Entities[roles[^1]];
                var route = await ControllerPlanning.RunAsync(t => SupplyLinePlanner.Segment(map, equipment.Belt, head, last.Direction, joined, pickup, t),
                    controller, TimeSpan.FromMinutes(2), token)
                    ?? await ClearedSegmentAsync(map, head, last.Direction, joined, pickup)
                    ?? throw new InvalidOperationException($"No trunk route from {head} toward the depot at {pickup} on observed terrain.");
                if (trunk + route.Belts.Count > SupplyLinePlanner.MaximumTrunkBelts)
                    throw new InvalidOperationException($"The trunk of row {row.Id} would exceed {SupplyLinePlanner.MaximumTrunkBelts} belts.");
                await journal.AppendAsync("supply-line-segment", new { line.Id, segment, head, belts = route.Belts.Count, route.Next, map.CollectedTick }, token);
                line = line with { Plan = With(Plan, route.Belts.Select((b, i) =>
                    new PlannedEntity(SupplyLinePlanner.TrunkRole(trunk + i), equipment.Belt, b.Position, b.Direction)).ToArray()) };
                await SaveAsync();
                await FlowAsync();
            }
        }

        /// <summary>A segment through trees and rocks, cleared before its belts are placed; null when even that leaves no route.</summary>
        private async Task<SupplyTrunkSegment?> ClearedSegmentAsync(SpatialSnapshot map, MapPosition head, int incoming, string joined, MapPosition pickup)
        {
            bool Removable(SpatialEntity e) => e.Id != map.Actor.Id && FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type);
            var open = map with { Entities = map.Entities.Where(e => !Removable(e)).ToArray() };
            var route = await ControllerPlanning.RunAsync(t => SupplyLinePlanner.Segment(open, equipment.Belt, head, incoming, joined, pickup, t),
                controller, TimeSpan.FromMinutes(2), token);
            if (route is null) return null;
            var tiles = route.Belts.Select(b => Tile(b.Position)).ToArray();
            var obstacles = map.Entities.Where(e => Removable(e) && tiles.Any(t => t.Overlaps(e.Bounds))).OrderBy(e => e.Position.DistanceTo(head)).ToArray();
            if (obstacles.Length > MaximumClearance) throw new InvalidOperationException($"The trunk segment would clear {obstacles.Length} obstacles.");
            foreach (var obstacle in obstacles)
            {
                await controller.TravelAsync(obstacle.Position, 2, catalog, token);
                var mined = await controller.WorkAsync("mine", new { name = obstacle.Name, position = obstacle.Position, count = 1 }, 1800, token: token);
                if (mined.Status is not ("completed" or "partial")) throw new InvalidOperationException($"Clearing {obstacle.Name} ended with {mined.Status}: {mined.Error?.Code}.");
                await journal.AppendAsync("supply-line-clearance", new { line = line.Id, obstacle.Id, obstacle.Name, obstacle.Position }, token);
            }
            return route;
        }

        /// <summary>The depot chest, then its inserter on the trunk's last belt, powered and natively proven to move belt to chest.</summary>
        private async Task DepotAsync()
        {
            foreach (string role in new[] { SupplyLinePlanner.DepotChestRole, SupplyLinePlanner.DepotInserterRole })
            {
                if (line.Entities.ContainsKey(role)) continue;
                await EnsureCarriedAsync(Plan[role].Item, 1);
                await PlaceAsync(role);
            }
            await PowerAsync(SupplyLinePlanner.DepotInserterRole);
            string last = line.Entities[SupplyLines.FlowRoles(Plan.Keys)[^1]];
            await VerifyArmAsync(SupplyLinePlanner.DepotInserterRole, last, line.Entities[SupplyLinePlanner.DepotChestRole]);
            var map = await MapAsync(32);
            if (map.Entities.SingleOrDefault(e => e.Id == last)?.BeltConnections is not { Outputs.Count: 0 })
                throw new InvalidDataException($"The trunk's last belt {last} feeds another belt instead of ending at the depot inserter.");
        }

        /// <summary>One feeder per ready cell with a planned feeder tile, each powered and natively proven to move chest to collector.</summary>
        private async Task FeedersAsync()
        {
            var ready = await ReadyCellsAsync();
            var cells = SupplyLines.FeederCells(Plan.Keys).Where(ready.ContainsKey).ToArray();
            int missing = cells.Count(i => !line.Entities.ContainsKey(SupplyLinePlanner.FeederRole(i)));
            if (missing > 0) await EnsureCarriedAsync(equipment.Inserter, missing);
            var belts = SupplyLines.FlowRoles(Plan.Keys).ToDictionary(r => Plan[r].Position, r => r);
            foreach (int index in cells)
            {
                string role = SupplyLinePlanner.FeederRole(index);
                if (!line.Entities.ContainsKey(role)) await PlaceAsync(role);
                await PowerAsync(role);
                if (!belts.TryGetValue(SupplyLinePlanner.DropTile(arm, Plan[role]), out string? collector) || !line.Entities.TryGetValue(collector, out string? belt))
                    throw new InvalidDataException($"The feeder of cell {index} drops beside the collector of row {row.Id}.");
                await VerifyArmAsync(role, ready[index], belt);
                if (line.Status == "ready") continue;
                line = line with { Status = "ready", Tick = (await MapAsync(4)).CollectedTick };
                await SaveAsync();
                await journal.AppendAsync("supply-line-ready", line, token);
            }
        }

        /// <summary>Joins an inserter of the line to a fed network unless a generator already feeds it; link poles join the line.</summary>
        private async Task PowerAsync(string role)
        {
            string id = line.Entities[role];
            if (FactoryPower.IsFed(await SnapshotAsync(), id) == true) return;
            await new CellPowerLinker(game, journal).ConnectAsync(id, Plan[role].Position, equipment.Pole, items, catalog, controller,
                [map => Protect(map, null), map => Protect(map, row)], count => EnsureCarriedAsync(equipment.Pole, count),
                async (linkId, link) =>
                {
                    line = FactoryCellBuilder.WithLink(line, linkId, link, equipment.Pole);
                    await SaveAsync();
                }, token);
        }

        /// <summary>Places a planned part, adopting one already standing there after an interruption, and registers it.</summary>
        private async Task<string> PlaceAsync(string role)
        {
            var planned = Plan[role];
            var map = await NearAsync(planned.Position);
            string name = map.Items[planned.Item].EntityName;
            var existing = map.Entities.FirstOrDefault(e => e.Name == name && e.Position.DistanceTo(planned.Position) < .01
                && (map.Prototypes[name].Type is "container" or "electric-pole" || e.Direction == planned.Direction));
            string id = existing?.Id ?? await new PoweredMachineController(game, journal).BuildAtAsync(planned.Item,
                new(planned.Position, planned.Direction, 0), catalog, controller, token);
            line = line with { Entities = new Dictionary<string, string>(line.Entities, StringComparer.Ordinal) { [role] = id } };
            await SaveAsync();
            return id;
        }

        /// <summary>The native belt graph continues the line through this belt: exactly its predecessor in, its successor out.</summary>
        private async Task VerifyLinkAsync(string? previous, string current, string? next)
        {
            var map = await MapAsync(32);
            ObservedBeltConnections Of(string id) => map.Entities.SingleOrDefault(e => e.Id == id)?.BeltConnections
                ?? throw new InvalidDataException($"The line belt {id} is not observed with native belt connections.");
            var connections = Of(current);
            bool linked = connections.Inputs.SequenceEqual(previous is null ? [] : [previous])
                && (previous is null || Of(previous).Outputs.SequenceEqual([current]))
                && (next is null || connections.Outputs.SequenceEqual([next]) && Of(next).Inputs.SequenceEqual([current]));
            if (!linked) throw new InvalidDataException($"The native belt graph around {current} does not continue the line "
                + $"(inputs {string.Join(",", connections.Inputs)}, outputs {string.Join(",", connections.Outputs)}).");
        }

        /// <summary>An inserter of the line natively picks from and drops into the planned entities; targets may resolve a few ticks late.</summary>
        private async Task VerifyArmAsync(string role, string pickupId, string dropId)
        {
            string id = line.Entities[role];
            for (int attempt = 0; ; attempt++)
            {
                var map = await NearAsync(Plan[role].Position);
                var hand = map.Entities.SingleOrDefault(e => e.Id == id) ?? throw new InvalidDataException($"The line inserter {id} is not observed.");
                if (hand.PickupTargetId == pickupId && hand.DropTargetId == dropId) return;
                if (attempt >= 3) throw new InvalidDataException($"The line inserter {id} moves from {hand.PickupTargetId} to {hand.DropTargetId}, not from {pickupId} to {dropId}.");
                var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 300, token: token);
                if (waited.Status != "completed") throw new InvalidOperationException($"Waiting for native inserter targets ended with {waited.Status}.");
            }
        }

        /// <summary>
        /// Observed ground for planning with every row and band reserved, the corridors at band walkway ends, the depot's planned tiles
        /// and steam growth. The open row keeps its walkway usable: its collector, feeders and their poles go there.
        /// </summary>
        private SpatialSnapshot Protect(SpatialSnapshot map, ResourceRow? open)
        {
            var depot = new[] { SupplyLinePlanner.DepotChestRole, SupplyLinePlanner.DepotInserterRole }.Where(Plan.ContainsKey).Select(r => Plan[r]);
            // The depot inserter's pickup tile is where the trunk ends: its standing arm becomes a reservation of its own tile.
            string? arm = line.Entities.GetValueOrDefault(SupplyLinePlanner.DepotInserterRole);
            var boxes = (state.Rows ?? []).SelectMany(r => r.Id == open?.Id ? ResourceCellPlanner.Footprints(map, r) : ResourceCellPlanner.Reservation(map, r))
                .Concat(state.Zones.SelectMany(z => SupplyLinePlanner.Corridors(Walkway(z)).Append(z.Box)))
                .Concat(depot.Select(p => Tile(p.Position)));
            var reserved = ResourceCellPlanner.Reserve(arm is null ? map : map with { Entities = map.Entities.Where(e => e.Id != arm).ToArray() },
                boxes, equipment.Pole);
            return steam is null ? reserved
                : PowerExpansionController.ReserveGrowth(reserved, steam, state.Zones, reserved.Entities.Single(e => e.Id == reserved.Actor.Id).Force);
        }

        /// <summary>Ready cells of the row with their output chest, by slot index.</summary>
        private async Task<IReadOnlyDictionary<int, string>> ReadyCellsAsync() => (await LoadAsync()).Cells
            .Where(c => c.IsResource && c.Slot.Band == row.Id && c.Status == "ready" && c.Entities.ContainsKey("output-chest"))
            .ToDictionary(c => c.Slot.Index, c => c.Entities["output-chest"]);

        private MapPosition Pickup() => SupplyLinePlanner.PickupTile(arm, Plan[SupplyLinePlanner.DepotInserterRole]);

        private Task EnsureCarriedAsync(string item, int count) =>
            new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(registry, catalog, item, count, token);

        /// <summary>A local observation around the position, travelling there first when it is out of reach.</summary>
        private async Task<SpatialSnapshot> NearAsync(MapPosition position)
        {
            var map = await MapAsync(32);
            if (map.Actor.Position.DistanceTo(position) <= 24) return map;
            await controller.TravelAsync(position, 6, catalog, token);
            return await MapAsync(32);
        }

        private async Task<SpatialSnapshot> MapAsync(int radius)
        {
            var map = await spatial.CaptureAsync(items, radius, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a supply line; reconcile partial construction.");
            return map;
        }

        private async Task<FactorySnapshot> SnapshotAsync()
        {
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a supply line; reconcile partial construction.");
            return snapshot;
        }

        private Task<FactoryState> LoadAsync() => registry.LoadAsync(catalog.Scope.WorldId, token);

        private async Task SaveAsync() => await registry.SaveAsync((await LoadAsync()).With(line), token);
    }

    private static Dictionary<string, PlannedEntity> With(IReadOnlyDictionary<string, PlannedEntity> plan, params PlannedEntity[] added)
    {
        var result = new Dictionary<string, PlannedEntity>(plan, StringComparer.Ordinal);
        foreach (var entity in added) result[entity.Role] = entity;
        return result;
    }

    private static WorldBox Walkway(FactoryZone zone) => FactoryBandPlanner.Walkway(zone.Origin, zone.Slots, zone.Pitch, zone.BandHeight);

    private static WorldBox Tile(MapPosition position) =>
        new(new(Math.Floor(position.X), Math.Floor(position.Y)), new(Math.Floor(position.X) + 1, Math.Floor(position.Y) + 1));

    private static MapPosition Centre(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);
}
