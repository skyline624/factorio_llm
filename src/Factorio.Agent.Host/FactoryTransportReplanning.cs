using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Revises unfinished single lines without losing their native paid equipment or unknown recovery outcomes.</summary>
internal sealed class FactoryTransportReplanning(IGameClient game, IControllerJournal journal, string directory)
{
    internal async Task<FactoryTransportBus> PrepareAsync(FactoryTransportBus bus, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (bus.Graph is not null || bus.Consumers.Count != 1 || bus.PendingRetirements is { Count: > 0 }) return bus;
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        if (cell.Status != "building" || cell.Plan is null) return bus;
        var local = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
        if (local.Scope != catalog.Scope) throw new InvalidDataException("Construction reservation inspection changed actor scope.");
        if (bus.ConstructionRoutingVersion >= 1 && cell.Plan.Values.All(p => TransportConstructionSafety.Allows(local, p.Position))) return bus;
        var snapshot = await CaptureAsync();
        var otherIds = state.Cells.Where(c => c.Id != cell.Id).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        if (cell.Entities.Values.Any(otherIds.Contains)) throw new InvalidDataException("A partial line shares another cell's native equipment.");
        cell = FactoryMaintenance.Reconcile(cell, snapshot, catalog, otherIds, removeMissing: true);
        state = state.With(cell);
        await registry.SaveAsync(state, token);
        var source = state.Cells.Single(c => c.Id == bus.SourceCellId);
        var target = state.Cells.Single(c => c.Id == bus.Consumers[0].TargetCellId);
        string[] required = FactoryTransportBuilder.FrameEntities(state, source, target, bus);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        var equipment = PowerFuelTransport.SelectEquipment(catalog) with { Inserter = cell.Plan!["source-inserter"].Item };
        string[] items = FactoryTransportBuilder.GeometryItems(state, steam, equipment.UndergroundBelt, equipment.Inserter);
        var builder = new FactoryTransportBuilder(game, journal, directory);
        SpatialSnapshot? map = FactoryTransportBuilder.PlanningCenter(snapshot, required, observationRadius: 64) is not null
            ? await builder.CaptureFuelFrameAsync(state, snapshot, required, steam, catalog, controller, token,
                equipment.UndergroundBelt, equipment.Inserter)
            : await new TransportCorridorSurvey(game, journal).CaptureAsync(snapshot, required, items, catalog, controller, token);
        if (map is null || map.Scope != catalog.Scope || required.Any(id => !map.Entities.Any(e => e.Id == id)))
            throw new InvalidOperationException("An unfinished line awaits a complete bounded actor survey before construction can resume.");
        // Include the current local sighting even when the survey ends far from the original failed approach.
        map = map with { StationaryThreats = (local.StationaryThreats ?? []).Concat(map.StationaryThreats ?? [])
            .GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.MaxBy(t => t.CollectedTick)!).ToArray() };
        if (cell.Plan.Values.All(p => TransportConstructionSafety.Allows(map, p.Position)))
        {
            bus = bus with { ConstructionRoutingVersion = 1 };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(bus), token);
            await journal.AppendAsync("factory-transport-construction-survey", new { bus.Id, map.CollectedTick,
                routeChanged = false, knownStationaryThreats = map.StationaryThreats.Count }, token);
            return bus;
        }
        var owned = cell.Entities.Values.ToHashSet(StringComparer.Ordinal);
        // Keep native poles as power anchors. Existing belts/arms will either match the new plan exactly or
        // be recovered from a durable list, so only those owned parts may be omitted from the search view.
        var searchMap = map with { Entities = map.Entities.Where(e => !owned.Contains(e.Id)
            || map.Prototypes[e.Name].Type == "electric-pole").ToArray() };
        searchMap = FactoryTransportBuilder.ProtectBands(searchMap, state, steam,
            source.IsResource ? new HashSet<int> { source.Slot.Band } : null);
        var output = cell.Plan["source-inserter"];
        var input = cell.Plan[bus.Consumers[0].InserterRole];
        if (input.Item != equipment.Inserter) throw new InvalidDataException("A single line must retain compatible native arm geometry.");
        var plan = await ControllerPlanning.RunAsync(t => new BeltTransportPlanner().Find(searchMap, equipment,
            source.Entities["output-chest"], target.Entities["input-chest"], t, maximumBelts: PowerFuelTransport.MaximumBelts,
            nodeBudget: 24000, sourceInserter: new(output.Position, output.Direction, 0),
            targetInserter: new(input.Position, input.Direction, 0)), controller, TimeSpan.FromSeconds(45), token);
        await journal.AppendAsync("factory-transport-reroute-search", new { bus.Id, map.CollectedTick,
            found = plan is not null, knownStationaryThreats = map.StationaryThreats.Count,
            maximumBelts = PowerFuelTransport.MaximumBelts, nodeBudget = 24000, budgetSeconds = 45 }, token);
        if (plan is null) throw new InvalidOperationException("No construction-safe replacement route exists in the bounded actor survey.");
        var record = CreateRecord(bus, cell, plan, map, equipment);
        snapshot = await CaptureAsync(record.Bus.PendingRetirements!.Select(r => r.Part.Item).Append(bus.Item).Distinct(StringComparer.Ordinal).ToArray());
        string? arm = cell.Entities.GetValueOrDefault("source-inserter");
        var retirements = record.Bus.PendingRetirements!;
        var incoming = retirements.Count == 0 ? null
            : FactoryTransportRecoveryCapacity.Incoming(snapshot, retirements, owned, bus.Item, PowerFuelTransport.MaximumBelts);
        if (!FactoryTransportConversion.SourceWiringIsExclusive(snapshot, source.Entities["output-chest"], arm,
                arm is not null && bus.ActorReserve is not null)
            || incoming is not null && !FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming))
            throw new InvalidOperationException("Partial transport rerouting awaits exclusive source wiring and shared native recovery capacity.");
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(record.Cell).With(record.Bus), token);
        await journal.AppendAsync("factory-transport-reroute-plan", new { bus = record.Bus, record.Cell.Plan,
            retained = record.Cell.Entities, previousPaidParts = cell.Entities.Count, map.CollectedTick }, token);
        return record.Bus;

        async Task<FactorySnapshot> CaptureAsync(IReadOnlyList<string>? carriedItems = null)
        {
            var captured = await new FactorySnapshotClient(game).CaptureAsync(carriedItems, cancellationToken: token);
            if (captured.Scope != catalog.Scope) throw new InvalidDataException("Partial transport inspection changed actor scope.");
            return captured;
        }
    }

    internal static (FactoryCell Cell, FactoryTransportBus Bus) CreateRecord(FactoryTransportBus bus, FactoryCell old,
        BeltTransportPlan plan, SpatialSnapshot map, BeltTransportEquipment equipment)
    {
        if (bus.Graph is not null || bus.Consumers.Count != 1 || bus.CellId != old.Id || old.Status != "building" || old.Plan is null
            || bus.PendingRetirements is { Count: > 0 } || old.Entities.Values.Distinct(StringComparer.Ordinal).Count() != old.Entities.Count)
            throw new InvalidDataException("Rerouting requires a distinct unfinished owned single line without unresolved retirements.");
        var record = FactoryTransportBuilder.NewBus(bus.SourceCellId, bus.Consumers[0].TargetCellId, bus.Item,
            bus.Consumers[0].Maximum, plan, map.CollectedTick, equipment);
        var parts = new Dictionary<string, PlannedEntity>(record.Cell.Plan!, StringComparer.Ordinal);
        var retained = new Dictionary<string, string>(StringComparer.Ordinal);
        string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
        foreach (var pair in old.Entities)
        {
            var native = map.Entities.Single(e => e.Id == pair.Value);
            var part = old.Plan[pair.Key];
            if (native.Name != map.Items[part.Item].EntityName || native.Position != part.Position || native.Force != force
                || !map.Bounds.Contains(native.Bounds) || map.Prototypes[native.Name].Type is not ("transport-belt" or "underground-belt" or "inserter" or "electric-pole")
                || native.Underground?.Type != part.UndergroundType)
                throw new InvalidDataException("Rerouting requires exact observed paid identities, geometry, ownership and underground types.");
            var matches = parts.Values.Where(p => map.Items[p.Item].EntityName == native.Name && p.Position == native.Position
                && p.Direction == native.Direction && p.UndergroundType == native.Underground?.Type).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("One native piece cannot acquire two replacement roles.");
            if (matches.Length == 1) retained[matches[0].Role] = native.Id;
            else if (map.Prototypes[native.Name].Type == "electric-pole")
            {
                string role = $"pole-{parts.Keys.Count(k => k.StartsWith("pole-", StringComparison.Ordinal))}";
                parts[role] = part with { Role = role, Direction = native.Direction };
                retained[role] = native.Id;
            }
        }
        foreach (string role in new[] { "source-inserter", bus.Consumers[0].InserterRole })
            if (old.Entities.TryGetValue(role, out string? id) && !retained.Values.Contains(id))
                throw new InvalidDataException("Rerouting must retain every paid source and consumer arm with its native stock circuit.");
        var pending = old.Entities.Where(p => !retained.Values.Contains(p.Value)).Select(p =>
        {
            var native = map.Entities.Single(e => e.Id == p.Value);
            return new FactoryTransportRetirement(native.Id, old.Plan[p.Key] with { Direction = native.Direction });
        }).ToArray();
        if (pending.Length > PowerFuelTransport.MaximumBelts) throw new InvalidDataException("Partial line recovery exceeds its equipment budget.");
        return (record.Cell with { Id = old.Id, Zone = old.Zone, Slot = old.Slot, Entities = retained, Plan = parts, Attempts = old.Attempts },
            record.Bus with { Id = bus.Id, CellId = old.Id, ActorReserve = pending.Length > 0 ? bus.ActorReserve ?? 0 : bus.ActorReserve,
                Consumers = [record.Bus.Consumers[0] with { Paused = bus.Consumers[0].Paused }], PendingRetirements = pending });
    }
}
