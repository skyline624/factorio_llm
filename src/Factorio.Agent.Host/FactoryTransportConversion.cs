using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FactoryTransportReadiness(int LegacyTwoConsumerBuses, bool SplitterEnabled, FactoryEquipmentUnlock[] UnlockResearch)
{
    public string Interpretation => "Healthy legacy two-consumer lines may need a splitter to distribute a shared item. Native recipe unlocks identify support research. Enabled equipment does not prove a feasible local conversion, installed branches or balanced measured throughput.";
}

/// <summary>Converts an owned native two-consumer line in place; its future graph and exact retirements are durable before work.</summary>
internal sealed class FactoryTransportConversion(IGameClient game, IControllerJournal journal, string directory)
{
    private static readonly BeltTransportEquipment Equipment = new("transport-belt", "inserter", "small-electric-pole");

    internal static FactoryTransportReadiness Readiness(FactoryState? state, FactorySnapshot snapshot, ProductionCatalog catalog,
        IReadOnlyDictionary<string, NativeTechnology> technologies)
    {
        int count = (state?.Transports ?? []).Count(b => b.Graph is null && b.Consumers.Count == 2
            && b.Consumers.All(c => !c.Paused) && FactoryTransportHealth.Healthy(state!, snapshot, b));
        bool enabled = FactoryDirector.Enabled(catalog, "splitter");
        return new(count, enabled, FactoryDirector.EquipmentUnlocks(catalog, technologies, count > 0 && !enabled ? ["splitter"] : []));
    }

    internal async Task<bool> PlanAsync(FactoryTransportBus bus, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (bus.Graph is not null || bus.PendingRetirements is { Count: > 0 } || bus.Consumers.Count != 2
            || bus.Consumers.Any(c => c.Paused) || !FactoryDirector.Enabled(catalog, "splitter")) return false;
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var source = state.Cells.Single(c => c.Id == bus.SourceCellId);
        var targets = bus.Consumers.Select(c => state.Cells.Single(t => t.Id == c.TargetCellId)).ToArray();
        if (targets.Any(t => !catalog.Recipes.Any(r => r.Name == t.Recipe && r.Ingredients.Any(i => i.DeterministicItem && i.Name == bus.Item))))
            return false;
        var snapshot = await CaptureAsync(catalog, token);
        if (!FactoryTransportHealth.Healthy(state, snapshot, bus)) return false;
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        if (cell.Entities.Values.Intersect(state.Cells.Where(c => c.Id != cell.Id).SelectMany(c => c.Entities.Values), StringComparer.Ordinal).Any())
            throw new InvalidDataException("Transport conversion cannot retire another cell's native equipment.");
        string[] required = FactoryTransportBuilder.FrameEntities(state, source, targets[0], bus);
        var center = FactoryTransportBuilder.PlanningCenter(snapshot, required);
        if (center is null) return false;
        await controller.TravelAsync(center, 4, catalog, token);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        string[] items = [.. FactoryTransportBuilder.GeometryItems(state, steam).Append("splitter").Distinct(StringComparer.Ordinal)];
        if (items.Length > 16) return false;
        var map = await new SpatialClient(game).CaptureAsync(items, 48, token);
        RequireScope(map.Scope, catalog);
        if (required.Any(id => !map.Entities.Any(e => e.Id == id && map.Bounds.Contains(e.Bounds)))) return false;
        snapshot = await CaptureAsync(catalog, token);
        if (!FactoryTransportHealth.Healthy(state, snapshot, bus)) return false;
        string[] belts = FactoryTransportHealth.Belts(cell).Select(r => cell.Entities[r]).ToArray();
        string arm = cell.Entities[bus.Consumers[0].InserterRole];
        int last = Array.IndexOf(belts, map.Entities.Single(e => e.Id == arm).PickupTargetId);
        if (last < 2) return false;
        string[] prefix = belts.Take(last + 1).ToArray();
        var poles = cell.Entities.Where(p => p.Key.StartsWith("pole-", StringComparison.Ordinal)).Select(p => p.Value).ToArray();
        var obsolete = belts.Skip(last + 1).Append(cell.Entities[bus.Consumers[1].InserterRole]).ToHashSet(StringComparer.Ordinal);
        map = FactoryTransportBuilder.ProtectBands(map, state, steam);
        var plan = await ControllerPlanning.RunAsync(t => new BalancedBeltPlanner().FindExisting(map, Equipment, "splitter",
            source.Entities["output-chest"], targets[0].Entities["input-chest"], targets[1].Entities["input-chest"],
            cell.Entities["source-inserter"], arm, prefix, poles, obsolete, t), controller, TimeSpan.FromSeconds(45), token);
        await journal.AppendAsync("factory-transport-conversion-search", new { bus.Id, map.Scope, map.CollectedTick, found = plan is not null }, token);
        if (plan is null) return false;
        var record = CreateRecord(bus, cell, plan, map);
        var pending = record.Bus.PendingRetirements!;
        var incoming = FactoryTransportRecoveryCapacity.Incoming(snapshot, pending, cell.Entities.Values.ToHashSet(StringComparer.Ordinal), bus.Item);
        snapshot = await CaptureAsync(catalog, token, incoming.Keys.ToArray());
        // Refresh stock after the capacity photograph: transit may have advanced since planning.
        incoming = FactoryTransportRecoveryCapacity.Incoming(snapshot, pending, cell.Entities.Values.ToHashSet(StringComparer.Ordinal), bus.Item);
        if (!FactoryTransportHealth.Healthy(state, snapshot, bus) || !FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming))
        {
            await journal.AppendAsync("factory-transport-conversion-deferred", new { bus.Id, snapshot.CollectedTick, reason = "native-health-or-recovery-capacity" }, token);
            return false;
        }
        await registry.SaveAsync(state.With(record.Cell).With(record.Bus), token);
        await journal.AppendAsync("factory-transport-conversion-plan", new { bus = record.Bus, record.Cell.Plan, retained = record.Cell.Entities, map.CollectedTick }, token);
        return true;
    }

    internal static (FactoryCell Cell, FactoryTransportBus Bus) CreateRecord(FactoryTransportBus bus, FactoryCell old,
        BalancedBeltPlan plan, SpatialSnapshot map)
    {
        if (bus.Graph is not null || bus.Consumers.Count != 2 || bus.CellId != old.Id || old.Plan is null
            || old.Entities.Values.Distinct(StringComparer.Ordinal).Count() != old.Entities.Count)
            throw new InvalidDataException("An in-place conversion needs a distinct owned legacy line.");
        var record = FactoryTransportBuilder.NewBalancedBus(bus.SourceCellId, bus.Consumers[0].TargetCellId, bus.Consumers[1].TargetCellId,
            bus.Item, bus.Consumers[0].Maximum, bus.Consumers[1].Maximum, plan, map.CollectedTick);
        var retained = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in record.Cell.Plan!.Values)
        {
            var matches = old.Entities.Values.Where(id => map.Entities.Any(e => e.Id == id
                && e.Name == map.Items[part.Item].EntityName && e.Position == part.Position && e.Direction == part.Direction)).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && retained.Values.Contains(matches[0]))
                throw new InvalidDataException("Conversion cannot reuse one native entity for two planned roles.");
            if (matches.Length == 1) retained[part.Role] = matches[0];
        }
        if (retained.GetValueOrDefault("source-inserter") != old.Entities["source-inserter"]
            || retained.GetValueOrDefault("target-inserter-0") != old.Entities[bus.Consumers[0].InserterRole]
            || old.Entities.Where(p => p.Key.StartsWith("pole-", StringComparison.Ordinal)).Any(p => !retained.Values.Contains(p.Value)))
            throw new InvalidDataException("Conversion must retain the original source, first consumer and every bus pole.");
        var pending = old.Entities.Where(p => !retained.Values.Contains(p.Value)).Select(p =>
        {
            var native = map.Entities.Single(e => e.Id == p.Value);
            var part = old.Plan[p.Key];
            if (native.Name != map.Items[part.Item].EntityName || !map.Bounds.Contains(native.Bounds)
                || map.Prototypes[native.Name].Type is not ("transport-belt" or "inserter"))
                throw new InvalidDataException("Only exact observed obsolete belts and consumer inserters can be retired.");
            return new FactoryTransportRetirement(p.Value, part with { Position = native.Position, Direction = native.Direction });
        }).ToArray();
        if (pending.Length is < 1 or > 201) throw new InvalidDataException("Conversion requires bounded exact native retirements.");
        return (record.Cell with { Id = old.Id, Entities = retained, Attempts = old.Attempts }, record.Bus with
        {
            Id = bus.Id, CellId = old.Id, ActorReserve = bus.ActorReserve ?? 0,
            Consumers = record.Bus.Consumers.Select((c, i) => c with { Paused = bus.Consumers[i].Paused }).ToArray(), PendingRetirements = pending
        });
    }

    internal async Task<FactoryTransportBus> RetireAsync(FactoryTransportBus bus, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (bus.PendingRetirements is not { Count: > 0 }) return bus;
        if (bus.Graph is null || bus.ActorReserve is null) throw new InvalidDataException("Recovery requires a recorded graph and restorable source stock control.");
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        string sourceArm = cell.Entities["source-inserter"], sourceChest = state.Cells.Single(c => c.Id == bus.SourceCellId).Entities["output-chest"];
        var source = await CaptureAsync(catalog, token);
        var nativeArm = source.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == sourceArm);
        if (nativeArm is null) throw new InvalidOperationException("The retained source inserter must be reconciled before conversion recovery.");
        var position = nativeArm.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        await controller.ApproachEntityAsync(sourceArm, position, catalog, token);
        await new FactoryTransportControl(game, journal).EnsureAsync(sourceArm, bus.Item, catalog, controller, token, sourceChest, 0, "<");
        foreach (var retirement in bus.PendingRetirements.ToArray())
        {
            bool recovered = false;
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            if (state.Cells.Where(c => c.Id != bus.CellId).Any(c => c.Entities.Values.Contains(retirement.EntityId))
                || cell.Entities.Values.Contains(retirement.EntityId))
                throw new InvalidDataException("A recorded retirement acquired another retained role.");
            var map = await ObservePartAsync();
            var target = map.Entities.SingleOrDefault(e => e.Id == retirement.EntityId);
            if (target is not null)
            {
                ValidatePart(target, map);
                await controller.ApproachEntityAsync(target.Id, target.Position, catalog, token);
                map = await ObservePartAsync();
                target = map.Entities.SingleOrDefault(e => e.Id == retirement.EntityId);
                if (target is not null)
                {
                    ValidatePart(target, map);
                    var all = cell.Entities.Values.Concat(bus.PendingRetirements!.Select(r => r.EntityId)).ToHashSet(StringComparer.Ordinal);
                    string[] items = [.. bus.PendingRetirements.Select(r => r.Part.Item).Append(bus.Item).Distinct(StringComparer.Ordinal)];
                    var snapshot = await CaptureAsync(catalog, token, items);
                    var armData = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == sourceArm).Data;
                    var control = armData.GetProperty("transport").GetProperty("inserterControl").Deserialize<ObservedInserterControl>(Protocol.Json);
                    if (!FactoryTransportControl.Matches(control, bus.Item, sourceChest, 0, "<"))
                        throw new InvalidDataException("Source feeding resumed before transport recovery.");
                    var incoming = FactoryTransportRecoveryCapacity.Incoming(snapshot, bus.PendingRetirements, all, bus.Item);
                    if (!FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming))
                        throw new InvalidOperationException("Transport recovery awaits enough shared actor inventory capacity.");
                    var receipt = await controller.WorkAsync("mine", new { entityId = target.Id, count = 1 }, 1800, token: token);
                    if (receipt.Status != "completed" || receipt.Effects.GetProperty("targetId").GetString() != target.Id
                        || receipt.Effects.GetProperty("product").GetString() != retirement.Part.Item
                        || receipt.Effects.GetProperty("produced").GetInt64() < 1)
                        throw new InvalidDataException("Transport retirement requires a completed matching native recovery receipt; reconcile before resuming.");
                    map = await ObservePartAsync();
                    if (map.Entities.Any(e => e.Id == retirement.EntityId)) throw new InvalidDataException("A recovered native bus piece is still present.");
                    recovered = true;
                }
            }
            bus = bus with { PendingRetirements = bus.PendingRetirements!.Where(r => r.EntityId != retirement.EntityId).ToArray() };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(bus), token);
            await journal.AppendAsync("factory-transport-piece-retired", new { bus.Id, retirement, map.CollectedTick, recovered,
                evidence = recovered ? "completed-native-mining-receipt-and-local-absence" : "complete-local-absence-no-recovery-claimed",
                remaining = bus.PendingRetirements.Count }, token);

            async Task<SpatialSnapshot> ObservePartAsync()
            {
                await controller.TravelAsync(retirement.Part.Position, 4, catalog, token);
                var captured = await new SpatialClient(game).CaptureAsync([retirement.Part.Item], 48, token);
                RequireScope(captured.Scope, catalog);
                var footprint = captured.Prototypes[captured.Items[retirement.Part.Item].EntityName].CollisionBox
                    .Rotate(retirement.Part.Direction).Translate(retirement.Part.Position);
                if (!captured.Bounds.Contains(footprint)) throw new InvalidDataException("Recovery requires complete local coverage of the recorded native footprint.");
                return captured;
            }

            void ValidatePart(SpatialEntity entity, SpatialSnapshot captured)
            {
                string force = captured.Entities.Single(e => e.Id == captured.Actor.Id).Force;
                if (entity.Name != captured.Items[retirement.Part.Item].EntityName || entity.Position != retirement.Part.Position
                    || entity.Direction != retirement.Part.Direction || entity.Force != force || !captured.Bounds.Contains(entity.Bounds))
                    throw new InvalidDataException("A recorded transport piece changed identity, geometry or ownership before retirement.");
            }
        }
        return bus;
    }

    private async Task<FactorySnapshot> CaptureAsync(ProductionCatalog catalog, CancellationToken token, IReadOnlyList<string>? items = null)
    {
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(items, cancellationToken: token);
        RequireScope(snapshot.Scope, catalog);
        return snapshot;
    }

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during transport conversion.");
    }
}
