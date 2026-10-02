using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Persistent, filtered single-item buses with native destination-stock limits. Routes are solved from observed geometry.</summary>
public sealed class FactoryTransportBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    private static readonly BeltTransportEquipment Equipment = new("transport-belt", "inserter", "small-electric-pole");
    private static readonly string[] Items = [Equipment.Belt, Equipment.Inserter, Equipment.Pole];

    public async Task<int> ConnectAsync(ProductionCatalog catalog, int maximumLinks = 2, CancellationToken token = default)
    {
        if (maximumLinks is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumLinks));
        if (Items.Any(i => !FactoryDirector.Enabled(catalog, i))) return 0;
        var registry = new FactoryRegistry(directory);
        await using var controller = new SpatialController(game, journal);
        int connected = 0, considered = 0;
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        foreach (var bus in (state.Transports ?? []).Where(b => state.Cells.Single(c => c.Id == b.CellId).Status == "building"))
        {
            await FinishAsync(bus, catalog, controller, token);
            if (++connected == maximumLinks) return connected;
        }
        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var shares = FactoryLogistics.CellShares(catalog, state);
        var stockSnapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (stockSnapshot.Scope != catalog.Scope) throw new InvalidDataException("Transport demand photograph belongs to another actor scope.");
        var pausedCells = FactoryLogistics.PausedCells(state.Cells.Where(c => c.Status == "ready"), catalog,
            FactoryLogistics.StockCaps(catalog, state), FactoryLogistics.AvailableStock(stockSnapshot))
            .Select(p => p.Cell.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var target in state.Cells.Where(c => c.Status == "ready" && c.Recipe is not null && c.Entities.ContainsKey("input-chest")))
        {
            if (pausedCells.Contains(target.Id)) continue;
            var recipe = catalog.Recipes.FirstOrDefault(r => r.Name == target.Recipe);
            if (recipe is null) continue;
            foreach (var ingredient in recipe.Ingredients.Where(i => i.DeterministicItem))
            {
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                if ((state.Transports ?? []).Any(b => b.Item == ingredient.Name && b.Consumers.Any(c => c.TargetCellId == target.Id))) continue;
                var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Factory transport actor scope changed.");
                var destination = Position(snapshot, target.Entities["input-chest"]);
                if (destination is null) continue;
                var sources = state.Cells.Where(c => c.Id != target.Id && c.Status == "ready" && c.Entities.ContainsKey("output-chest")
                    && Product(catalog, c) == ingredient.Name).Select(c => (Cell: c, Position: Position(snapshot, c.Entities["output-chest"])))
                    .Where(p => p.Position is not null).OrderBy(p => p.Position!.DistanceTo(destination)).ToArray();
                foreach (var candidate in sources)
                {
                    // A local proof cannot invent tiles between distant installations. Other links retain actor logistics.
                    if (candidate.Position!.DistanceTo(destination) > 64) continue;
                    if (++considered > 8) return connected;
                    int limit = checked((int)Math.Clamp(ingredient.Amount!.Value * FactoryLogistics.CellBufferCrafts(target, shares, 40), 1, 10000));
                    if (await LinkAsync(candidate.Cell.Id, target.Id, ingredient.Name, limit, catalog, controller, token))
                    {
                        if (++connected == maximumLinks) return connected;
                        break;
                    }
                }
            }
        }
        return connected;
    }

    public async Task<bool> LinkAsync(string sourceCellId, string targetCellId, string item, int maximum, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (maximum is < 1 or > 10000 || !catalog.Items.ContainsKey(item)) throw new ArgumentException("Invalid transport stock limit or item.");
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var source = state.Cells.Single(c => c.Id == sourceCellId);
        var target = state.Cells.Single(c => c.Id == targetCellId);
        if (source.Status != "ready" || target.Status != "ready" || Product(catalog, source) != item
            || !source.Entities.ContainsKey("output-chest") || !target.Entities.ContainsKey("input-chest")
            || !catalog.Recipes.Any(r => r.Name == target.Recipe && r.Ingredients.Any(i => i.DeterministicItem && i.Name == item)))
            throw new InvalidOperationException("A bus must connect a registered producer to its native recipe consumer.");
        var bus = (state.Transports ?? []).SingleOrDefault(b => b.SourceCellId == sourceCellId && b.Item == item);
        if (bus is not null && bus.Consumers.Any(c => c.TargetCellId == targetCellId))
        {
            if (state.Cells.Single(c => c.Id == bus.CellId).Status == "building") await FinishAsync(bus, catalog, controller, token);
            return true;
        }
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Factory transport scope changed.");
        if (bus is not null && !FactoryTransportHealth.Healthy(state, snapshot, bus)) return false;
        var from = Position(snapshot, source.Entities["output-chest"]);
        var to = Position(snapshot, target.Entities["input-chest"]);
        if (from is null || to is null || from.DistanceTo(to) > 64) return false;
        await controller.ApproachEntityAsync(source.Entities["output-chest"], from, catalog, token);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        var ground = new FactoryGround(state, steam);
        string[] geometryItems = [.. Items.Concat(ground.Items).Concat(state.Cells.SelectMany(c => c.Plan?.Values ?? []).Select(p => p.Item))
            .Distinct(StringComparer.Ordinal)];
        var map = await new SpatialClient(game).CaptureAsync(geometryItems, 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Transport planning scope changed.");
        if (!map.Entities.Any(e => e.Id == target.Entities["input-chest"]) || map.Prototypes[map.Items[Equipment.Inserter].EntityName].FilterSlots is not > 0)
            return false;
        map = ProtectBands(map, state, steam);
        var plans = bus is null ? new Dictionary<string, PlannedEntity>(StringComparer.Ordinal)
            : new Dictionary<string, PlannedEntity>(state.Cells.Single(c => c.Id == bus.CellId).Plan!, StringComparer.Ordinal);
        string consumerRole = $"target-inserter-{bus?.Consumers.Count ?? 0}";
        if (bus is null)
        {
            var plan = new BeltTransportPlanner().Find(map, Equipment, source.Entities["output-chest"], target.Entities["input-chest"], token);
            if (plan is null) return false;
            Add("source-inserter", Equipment.Inserter, plan.SourceInserter);
            Add(consumerRole, Equipment.Inserter, plan.TargetInserter);
            for (int i = 0; i < plan.Belts.Count; i++) Add($"belt-{i}", Equipment.Belt, plan.Belts[i]);
            for (int i = 0; i < plan.Poles.Count; i++) Add($"pole-{i}", Equipment.Pole, plan.Poles[i]);
        }
        else
        {
            var current = state.Cells.Single(c => c.Id == bus.CellId);
            var roles = FactoryTransportHealth.Belts(current);
            if (roles.Any(r => !map.Entities.Any(e => e.Id == current.Entities[r]))) return false;
            var plan = new FactoryBeltPlanner().Extend(map, Equipment, roles.Select(r => current.Entities[r]).ToArray(), target.Entities["input-chest"], token);
            if (plan is null) return false;
            Add(consumerRole, Equipment.Inserter, plan.TargetInserter);
            Add(roles[^1], Equipment.Belt, plan.Belts[0]);
            for (int i = 1; i < plan.Belts.Count; i++) Add($"belt-{roles.Length + i - 1}", Equipment.Belt, plan.Belts[i]);
            int poles = plans.Keys.Count(k => k.StartsWith("pole-", StringComparison.Ordinal));
            for (int i = 0; i < plan.Poles.Count; i++) Add($"pole-{poles + i}", Equipment.Pole, plan.Poles[i]);
        }
        string cellId = bus?.CellId ?? $"transport-{Guid.NewGuid():N}";
        var cell = bus is null ? new FactoryCell(cellId, 0, new(0, 0, true), "transport", Equipment.Belt, null,
            new Dictionary<string, string>(), "building", map.CollectedTick, Plan: plans)
            : state.Cells.Single(c => c.Id == cellId) with { Plan = plans, Status = "building" };
        bus = bus is null ? new FactoryTransportBus($"bus-{Guid.NewGuid():N}", sourceCellId, item, cellId, [new(targetCellId, consumerRole, maximum)])
            : bus with { Consumers = [.. bus.Consumers, new(targetCellId, consumerRole, maximum)] };
        // Save the complete new graph before its first mutation, including a terminal belt's future direction on extension.
        await registry.SaveAsync(state.With(cell).With(bus), token);
        await journal.AppendAsync("factory-transport-plan", new { bus, cell.Plan, map.CollectedTick }, token);
        await FinishAsync(bus, catalog, controller, token);
        return true;

        void Add(string role, string equipment, PlacementCandidate p) => plans[role] = new(role, equipment, p.Position, p.Direction);
    }

    public async Task RepairControlsAsync(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        foreach (var bus in state.Transports ?? [])
        {
            var cell = state.Cells.Single(c => c.Id == bus.CellId);
            if (cell.Status != "ready" || cell.Entities.Values.Any(id => Position(snapshot, id) is null)
                || FactoryTransportHealth.Healthy(state, snapshot, bus)) continue;
            await ConfigureAsync(bus, state, catalog, controller, token);
        }
    }

    public async Task<FactoryState> ApplyPausesAsync(FactoryState state, IReadOnlySet<string> pausedCells, CancellationToken token)
    {
        bool changed = false;
        foreach (var bus in state.Transports ?? [])
        {
            var consumers = bus.Consumers.Select(c => c with { Paused = pausedCells.Contains(c.TargetCellId) }).ToArray();
            if (consumers.SequenceEqual(bus.Consumers)) continue;
            state = state.With(bus with { Consumers = consumers });
            changed = true;
        }
        if (changed) await new FactoryRegistry(directory).SaveAsync(state, token);
        return state;
    }

    private async Task FinishAsync(FactoryTransportBus bus, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        token = deadline.Token;
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        cell = FactoryMaintenance.Reconcile(cell, snapshot, catalog, state.Cells.Where(c => c.Id != cell.Id)
            .SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal), removeMissing: true);
        await registry.SaveAsync(state.With(cell), token);
        var missing = cell.Plan!.Values.Where(p => !cell.Entities.ContainsKey(p.Role)).GroupBy(p => p.Item)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        await new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(registry, catalog, missing, token);
        var ids = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        foreach (var p in cell.Plan.Values.OrderBy(p => p.Role.StartsWith("pole-", StringComparison.Ordinal) ? 0
            : p.Role.StartsWith("target-inserter-", StringComparison.Ordinal) ? 1 : p.Role == "source-inserter" ? 3 : 2))
        {
            if (ids.ContainsKey(p.Role)) continue;
            string id = await new PoweredMachineController(game, journal).BuildAtAsync(p.Item, new(p.Position, p.Direction, 0), catalog, controller, token,
                stoppedInserterItem: p.Item == Equipment.Inserter ? bus.Item : null);
            ids[p.Role] = id;
            cell = cell with { Entities = new Dictionary<string, string>(ids, StringComparer.Ordinal) };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        }
        state = (await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell with { Status = "ready" });
        await ConfigureAsync(bus, state, catalog, controller, token);
        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (!FactoryTransportHealth.Healthy(state, snapshot, bus))
            throw new InvalidDataException("The built bus lacks a matching native graph, filtered stock control or fed endpoints.");
        cell = cell with { Status = "ready", Tick = snapshot.CollectedTick };
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        await journal.AppendAsync("factory-transport-ready", new { bus, cell, snapshot.CollectedTick }, token);
    }

    private async Task ConfigureAsync(FactoryTransportBus bus, FactoryState state, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        var control = new FactoryTransportControl(game, journal);
        foreach (var consumer in bus.Consumers)
        {
            var target = state.Cells.Single(c => c.Id == consumer.TargetCellId);
            if (!target.Entities.TryGetValue("input-chest", out var chest)) return;
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (Position(snapshot, chest) is null) return;
            var position = Position(snapshot, cell.Entities[consumer.InserterRole])!;
            await controller.ApproachEntityAsync(cell.Entities[consumer.InserterRole], position, catalog, token);
            await control.EnsureAsync(cell.Entities[consumer.InserterRole], bus.Item, catalog, controller, token, chest, consumer.Paused ? 0 : consumer.Maximum);
        }
        foreach (var role in FactoryTransportHealth.Belts(cell))
        {
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var record = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == cell.Entities[role]);
            if (record.Data.GetProperty("direction").GetInt32() == cell.Plan![role].Direction) continue;
            await OrientAsync(cell.Entities[role], cell.Plan[role].Direction, catalog, controller, token);
        }
        var sourceSnapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        await controller.ApproachEntityAsync(cell.Entities["source-inserter"], Position(sourceSnapshot, cell.Entities["source-inserter"])!, catalog, token);
        await control.EnsureAsync(cell.Entities["source-inserter"], bus.Item, catalog, controller, token);
    }

    private async Task OrientAsync(string id, int direction, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        int? expected = null;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var data = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == id).Data;
            int native = data.GetProperty("direction").GetInt32();
            if (expected is not null && native != expected) throw new InvalidDataException("Belt direction differs from its rotation receipt.");
            if (native == direction) return;
            if (attempt == 3) break;
            await controller.ApproachEntityAsync(id, data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, catalog, token);
            var receipt = await controller.WorkAsync("rotate", new { entityId = id }, 600, token: token);
            if (receipt.Status != "completed" || receipt.Effects.GetProperty("beforeDirection").GetInt32() != native)
                throw new InvalidDataException("Belt orientation requires a matching completed rotation receipt.");
            expected = receipt.Effects.GetProperty("afterDirection").GetInt32();
        }
        throw new InvalidOperationException("Belt did not reach its planned cardinal direction.");
    }

    internal static SpatialSnapshot ProtectBands(SpatialSnapshot map, FactoryState state, PowerExpansionController.SteamItems? steam = null)
    {
        var boxes = new List<WorldBox>();
        foreach (var zone in state.Zones)
        {
            int h = (zone.BandHeight - 6) / 2;
            for (int i = 0; i < zone.Slots; i++)
                foreach (bool north in new[] { true, false })
                {
                    if (state.Cells.Any(c => c.Zone == zone.Id && c.Slot.Index == i && c.Slot.North == north)) continue;
                    double top = zone.Origin.Y + (north ? 0 : h + 4);
                    boxes.Add(new(new(zone.Origin.X + i * zone.Pitch, top), new(zone.Origin.X + (i + 1) * zone.Pitch, top + h + 2)));
                }
        }
        boxes.AddRange(state.Cells.Where(c => c.Status == "building" && c.Kind != "transport").SelectMany(c => c.Plan?.Values ?? [])
            .Where(p => map.Items.ContainsKey(p.Item)).Select(p => map.Prototypes[map.Items[p.Item].EntityName].CollisionBox.Rotate(p.Direction).Translate(p.Position)));
        boxes.AddRange((state.Rows ?? []).SelectMany(r => ResourceCellPlanner.Reservation(map, r)));
        if (steam is not null)
            boxes.AddRange(PowerExpansionController.ReserveGrowth(map, steam, state.Zones,
                map.Entities.Single(e => e.Id == map.Actor.Id).Force).Entities.Skip(map.Entities.Count).Select(e => e.Bounds));
        return FactoryGround.Reserve(map, boxes, Equipment.Belt);
    }

    private static string? Product(ProductionCatalog catalog, FactoryCell cell) => cell.Recipe is null ? null
        : catalog.Recipes.FirstOrDefault(r => r.Name == cell.Recipe)?.Products.FirstOrDefault(p => p.DeterministicItem)?.Name
            ?? (cell.IsResource ? cell.Recipe : null);
    private static MapPosition? Position(FactorySnapshot snapshot, string id) => snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.EntityId == id)
        ?.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json);
}
