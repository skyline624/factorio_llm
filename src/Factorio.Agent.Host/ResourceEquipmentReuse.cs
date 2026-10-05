using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

internal sealed record ReusableResourceEquipment(string CellId, string Role, string Item, string EntityId, MapPosition Position);
internal sealed record ResourceRecoveryDeposit(string EntityId, MapPosition Position, string Item, int Count);

/// <summary>Recovers needed drills and idle furnaces from exhausted cells, leaving chests, arms and the power network standing.</summary>
internal sealed class ResourceEquipmentReuse(IGameClient game, IControllerJournal journal)
{
    internal const int MaximumParts = 4;
    internal const double MaximumDistance = 96;
    internal const double MaximumStorageDistance = 384;
    internal const int MaximumDeposits = 6;

    public async Task RecoverAsync(FactoryRegistry registry, ProductionCatalog catalog, IReadOnlyDictionary<string, int> needed,
        CancellationToken token)
    {
        if (!needed.Any(p => p.Value > 0 && Recoverable(catalog, p.Key))) return;
        await using var controller = new SpatialController(game, journal);
        var reader = new FactorySnapshotClient(game);
        for (int count = 0; count < MaximumParts; count++)
        {
            var snapshot = await CaptureAsync();
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var candidates = Candidates(state, snapshot, catalog, needed);
            var zones = game is IDangerZoneReader danger
                ? await danger.ReadActiveDeathsAsync(catalog.Scope, 1, snapshot.CollectedTick, token) : [];
            var part = candidates.FirstOrDefault(p => ResourceCellBuilder.Safe(state.Cells.Single(c => c.Id == p.CellId), zones));
            if (part is null) return;
            snapshot = await CaptureAsync(Incoming(snapshot, part).Keys.ToArray());
            for (int deposit = 0; !FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, Incoming(snapshot, part))
                && deposit < MaximumDeposits; deposit++)
            {
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                var items = FactoryLogistics.Surplus(FactoryLogistics.Carried(snapshot),
                    item => Math.Max(needed.GetValueOrDefault(item), FactoryLogistics.CollectCap(0, catalog.Items[item].StackSize)),
                    item => catalog.Items[item].StackSize);
                ResourceRecoveryDeposit? home = null;
                foreach (var (item, _) in items)
                {
                    snapshot = await CaptureAsync(Incoming(snapshot, part).Keys.Append(item).Distinct(StringComparer.Ordinal).ToArray());
                    home = DepositOptions(state, snapshot, catalog, needed).FirstOrDefault(p => p.Item == item);
                    if (home is not null) break;
                }
                if (home is null) break;
                await controller.ApproachEntityAsync(home.EntityId, home.Position, catalog, token);
                snapshot = await CaptureAsync(Incoming(snapshot, part).Keys.Append(home.Item).Distinct(StringComparer.Ordinal).ToArray());
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                home = DepositOptions(state, snapshot, catalog, needed).FirstOrDefault(p => p.EntityId == home.EntityId && p.Item == home.Item);
                if (home is null) break;
                var deposited = await controller.WorkAsync("insert", new { entityId = home.EntityId, inventory = "chest", item = home.Item, count = home.Count },
                    600, token: token);
                long moved = Transfer(deposited, home.EntityId, home.Item, home.Count, "from_actor");
                await journal.AppendAsync("resource-equipment-room-deposit", new { home, moved, deposited }, token);
                snapshot = await CaptureAsync(Incoming(snapshot, part).Keys.ToArray());
                if (moved == 0) break;
            }
            if (!FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, Incoming(snapshot, part)))
            {
                await journal.AppendAsync("resource-equipment-recovery-deferred", new { part, snapshot.CollectedTick, reason = "joint-native-inventory-capacity" }, token);
                return;
            }
            await controller.ApproachEntityAsync(part.EntityId, part.Position, catalog, token);
            snapshot = await CaptureAsync(Incoming(snapshot, part).Keys.ToArray());
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            if (!Candidates(state, snapshot, catalog, needed).Contains(part)) continue;
            if (!FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, Incoming(snapshot, part)))
            {
                await journal.AppendAsync("resource-equipment-recovery-deferred", new { part, snapshot.CollectedTick, reason = "capacity-changed-during-travel" }, token);
                return;
            }
            var map = await new SpatialClient(game).CaptureAsync(radius: 32, cancellationToken: token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed before resource equipment recovery.");
            var cell = state.Cells.Single(c => c.Id == part.CellId);
            // No replacement drill or another machine may have resumed feeding this idle furnace during travel.
            var target = map.Entities.Single(e => e.Id == part.EntityId);
            if (part.Role == "furnace" && map.Entities.Any(e => (e.DropTargetId == part.EntityId
                    || e.DropPosition is { } drop && target.Bounds.Contains(drop))
                && (e.Id != cell.Entities.GetValueOrDefault("drill") || e.Id != ExhaustedDrill(snapshot, cell)))) return;
            await journal.AppendAsync("resource-equipment-recovery-plan", new { part, snapshot.Scope, snapshot.CollectedTick }, token);
            foreach (var fuel in Fuel(snapshot, part.EntityId))
            {
                var taken = await controller.WorkAsync("take", new { entityId = part.EntityId, inventory = "fuel", item = fuel.Key, count = fuel.Value },
                    600, token: token);
                long moved = Transfer(taken, part.EntityId, fuel.Key, fuel.Value, "to_actor");
                if (moved != fuel.Value)
                {
                    await journal.AppendAsync("resource-equipment-recovery-deferred", new { part, reason = "fuel-transfer-terminal-shortfall", moved, taken }, token);
                    return;
                }
            }
            snapshot = await CaptureAsync();
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            if (!Candidates(state, snapshot, catalog, needed).Contains(part) || Fuel(snapshot, part.EntityId).Count != 0) return;
            long carriedBefore = FactoryLogistics.Carried(snapshot).GetValueOrDefault(part.Item);
            var receipt = await controller.WorkAsync("mine", new { entityId = part.EntityId, count = 1 }, 1800, token: token);
            var after = await CaptureAsync();
            if (receipt.Status == "failed" && receipt.Error?.Code == "inventory_full" && receipt.Kind == "mine"
                && receipt.Effects.GetProperty("targetId").GetString() == part.EntityId
                && receipt.Effects.GetProperty("product").GetString() == part.Item && receipt.Effects.GetProperty("produced").GetInt64() == 0
                && after.Records.Any(r => r.Kind == "entity" && r.EntityId == part.EntityId)
                && FactoryLogistics.Carried(after).GetValueOrDefault(part.Item) == carriedBefore)
            {
                await journal.AppendAsync("resource-equipment-recovery-deferred", new { part, reason = "terminal-inventory-full", receipt }, token);
                return;
            }
            if (receipt.Status != "completed" || receipt.Effects.GetProperty("targetId").GetString() != part.EntityId
                || receipt.Effects.GetProperty("product").GetString() != part.Item || receipt.Effects.GetProperty("produced").GetInt64() != 1
                || after.Records.Any(r => r.Kind == "entity" && r.EntityId == part.EntityId)
                || FactoryLogistics.Carried(after).GetValueOrDefault(part.Item) < carriedBefore + 1)
                throw new InvalidDataException("Resource equipment recovery is unconfirmed; reconcile before continuing.");
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            cell = state.Cells.Single(c => c.Id == part.CellId);
            if (cell.Status != ResourceCellHealth.Depleted || cell.Entities.GetValueOrDefault(part.Role) != part.EntityId)
                throw new InvalidDataException("Resource cell changed during equipment recovery.");
            var standing = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
            standing.Remove(part.Role);
            await registry.SaveAsync(state.With(cell with { Entities = standing }), token);
            await journal.AppendAsync("resource-equipment-recovered", new { part, after.Scope, after.CollectedTick, receipt }, token);
        }

        async Task<FactorySnapshot> CaptureAsync(IReadOnlyList<string>? capacityItems = null)
        {
            var snapshot = await reader.CaptureAsync(capacityItems, cancellationToken: token);
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during resource equipment recovery.");
            return snapshot;
        }
    }

    internal static IReadOnlyDictionary<string, long> Incoming(FactorySnapshot snapshot, ReusableResourceEquipment part)
    {
        var incoming = Fuel(snapshot, part.EntityId);
        incoming[part.Item] = checked(incoming.GetValueOrDefault(part.Item) + 1);
        // Leave one of the eight native probes available for a candidate surplus item.
        if (incoming.Count > 7 || incoming.Any(p => p.Value < 1)) throw new InvalidDataException("Unbounded resource equipment recovery stock.");
        return incoming;
    }

    /// <summary>Returns only observed, compatible producer chests with native room; a full nearest chest cannot hide another.</summary>
    internal static IReadOnlyList<ResourceRecoveryDeposit> DepositOptions(FactoryState state, FactorySnapshot snapshot,
        ProductionCatalog catalog, IReadOnlyDictionary<string, int> needed, double maximumDistance = MaximumDistance)
    {
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0 || maximumDistance > MaximumStorageDistance)
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        if (snapshot.Scope != catalog.Scope || snapshot.Scope.WorldId != state.WorldId)
            throw new InvalidDataException("Resource recovery deposits require one observed world and actor.");
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        var at = actor.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        var bag = FactoryLogistics.Carried(snapshot);
        var result = new List<ResourceRecoveryDeposit>();
        foreach (var (item, amount) in bag)
        {
            if (!catalog.Items.TryGetValue(item, out var native)) continue;
            long surplus = amount - Math.Max(needed.GetValueOrDefault(item), FactoryLogistics.CollectCap(0, native.StackSize));
            if (surplus <= 0) continue;
            foreach (var cell in state.Cells.Where(c => c.Recipe is not null && c.Entities.ContainsKey("output-chest")
                && (catalog.Recipes.FirstOrDefault(r => r.Name == c.Recipe)?.Products[0].Name ?? c.Recipe) == item))
            {
                string id = cell.Entities["output-chest"];
                var entity = snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == id);
                var inventory = snapshot.Records.SingleOrDefault(r => r.Kind == "inventory" && r.EntityId == id && r.Name == "chest");
                if (entity is null || entity.Data.GetProperty("role").GetString() != "factory" || inventory is null
                    || !inventory.Data.TryGetProperty("capacityHints", out var hints) || !hints.TryGetProperty(item, out var hint)) continue;
                if (hint.GetProperty("certainty").GetString() != "native-estimate" || !hint.GetProperty("insertable").TryGetInt64(out long capacity)
                    || capacity < 0) throw new InvalidDataException("Invalid native surplus storage capacity.");
                var position = entity.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                if (capacity == 0 || !hint.GetProperty("canInsertOne").GetBoolean() || position.DistanceTo(at) > maximumDistance) continue;
                result.Add(new(id, position, item, checked((int)Math.Min(1000, Math.Min(surplus, capacity)))));
            }
        }
        return result.Distinct().OrderByDescending(p => (double)p.Count / catalog.Items[p.Item].StackSize)
            .ThenBy(p => p.Position.DistanceTo(at)).ThenBy(p => p.EntityId, StringComparer.Ordinal).ToArray();
    }

    internal static long Transfer(OperationReceipt receipt, string id, string item, long requested, string direction)
    {
        if (receipt.Kind != (direction == "to_actor" ? "take" : "insert")
            || receipt.Effects.GetProperty("targetId").GetString() != id || receipt.Effects.GetProperty("item").GetString() != item
            || receipt.Effects.GetProperty("requested").GetInt64() != requested || receipt.Effects.GetProperty("direction").GetString() != direction)
            throw new InvalidDataException("Resource recovery transfer lacks matching native effects.");
        long moved = receipt.Effects.GetProperty("transferred").GetInt64();
        if (receipt.Status == "completed" && moved == requested || receipt.Status == "partial" && moved > 0 && moved < requested
            || receipt.Status == "failed" && receipt.Error?.Code == "transfer_blocked" && moved == 0) return moved;
        throw new InvalidDataException("Resource recovery transfer is unconfirmed; reconcile before continuing.");
    }

    internal static IReadOnlyList<ReusableResourceEquipment> Candidates(FactoryState state, FactorySnapshot snapshot,
        ProductionCatalog catalog, IReadOnlyDictionary<string, int> needed)
    {
        if (snapshot.Scope != catalog.Scope || snapshot.Scope.WorldId != state.WorldId)
            throw new InvalidDataException("Resource equipment recovery requires one observed actor and world.");
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        var position = actor.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        var carried = FactoryLogistics.Carried(snapshot);
        var entities = snapshot.Records.Where(r => r.Kind == "entity").ToDictionary(r => r.EntityId, StringComparer.Ordinal);
        var claimed = state.Cells.SelectMany(c => c.Entities.Values).GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<ReusableResourceEquipment>();
        foreach (var cell in state.Cells.Where(c => c.IsResource && c.Status == ResourceCellHealth.Depleted && c.Plan is not null))
            foreach (string role in new[] { "furnace", "drill" })
            {
                if (!cell.Entities.TryGetValue(role, out var id) || !cell.Plan!.TryGetValue(role, out var plan)
                    || needed.GetValueOrDefault(plan.Item) <= carried.GetValueOrDefault(plan.Item) || !Recoverable(catalog, plan.Item)
                    || claimed[id] != 1 || !entities.TryGetValue(id, out var native)
                    || native.Data.GetProperty("role").GetString() != "factory" || native.Name != catalog.Items[plan.Item].PlaceEntity
                    || native.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json) is not { } location
                    || location.DistanceTo(plan.Position) >= .01 || location.DistanceTo(position) > MaximumDistance) continue;
                bool drillPresent = cell.Entities.TryGetValue("drill", out var drill) && entities.ContainsKey(drill);
                if (drillPresent && ExhaustedDrill(snapshot, cell) is null || role == "drill" && !drillPresent) continue;
                var work = snapshot.Records.SingleOrDefault(r => r.Kind == "work" && r.EntityId == id);
                if (role == "furnace" && (work is null || !work.Data.TryGetProperty("inProcess", out var running) || running.GetBoolean())) continue;
                var inventories = snapshot.Records.Where(r => r.Kind == "inventory" && r.EntityId == id).ToArray();
                if (native.Data.GetProperty("inventories").ValueKind == JsonValueKind.Array
                    && native.Data.GetProperty("inventories").EnumerateArray().Any(i => !inventories.Any(r => r.Id == i.GetString()))) continue;
                if (inventories.Any(r => r.Name != "fuel" && Stocked(r.Data, "items"))
                    || inventories.Any(r => r.Data.TryGetProperty("stacks", out var stacks) && stacks.ValueKind == JsonValueKind.Array
                        && stacks.EnumerateArray().Any(s => s.GetProperty("quality").GetString() != "normal"))
                    || snapshot.Records.Any(r => r.Kind == "transit" && r.EntityId == id && Stocked(r.Data, "items"))
                    || snapshot.FluidRecordsAt(id).Any(r => Stocked(r.Data, "contents"))
                    || !native.Data.TryGetProperty("transport", out var transport) || transport.ValueKind != JsonValueKind.Object
                    || !transport.TryGetProperty("redNeighbourCount", out var red) || red.GetInt32() != 0
                    || !transport.TryGetProperty("greenNeighbourCount", out var green) || green.GetInt32() != 0) continue;
                result.Add(new(cell.Id, role, plan.Item, id, location));
            }
        return result.OrderBy(p => p.Role == "furnace" ? 0 : 1).ThenBy(p => p.Position.DistanceTo(position))
            .ThenBy(p => p.EntityId, StringComparer.Ordinal).ToArray();
    }

    private static bool Recoverable(ProductionCatalog catalog, string item) => catalog.Items.TryGetValue(item, out var native)
        && native.PlaceEntityType is "furnace" or "mining-drill";

    private static string? ExhaustedDrill(FactorySnapshot snapshot, FactoryCell cell) => cell.Entities.TryGetValue("drill", out var id)
        && snapshot.Records.Any(r => r.Kind == "work" && r.EntityId == id && r.Data.TryGetProperty("statusName", out var status)
            && status.GetString() == "no_minable_resources") ? id : null;

    private static bool Stocked(JsonElement data, string property) => data.GetProperty(property).EnumerateObject().Any(p => p.Value.GetDouble() > 0);

    private static Dictionary<string, long> Fuel(FactorySnapshot snapshot, string id) => snapshot.Records
        .Where(r => r.Kind == "inventory" && r.EntityId == id && r.Name == "fuel")
        .SelectMany(r => r.Data.GetProperty("items").EnumerateObject()).ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
}
