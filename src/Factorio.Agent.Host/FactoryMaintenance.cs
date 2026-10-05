using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record MissingEntity(FactoryCell Cell, string Role, string PreviousId, PlannedEntity Plan);
public sealed record DegradedEntity(string Role, string PreviousId, string? Item);
public sealed record DegradedCell(string Cell, IReadOnlyList<DegradedEntity> Missing);
public sealed record MaintenanceResult(IReadOnlyList<string> Rebuilt, IReadOnlyList<string> Blocked,
    IReadOnlyDictionary<string, long> Supplied, IReadOnlyDictionary<string, long> Shortfall, int Actions, long Tick,
    IReadOnlyList<string> Unpowered, IReadOnlyList<string> RecoveredPlans);

/// <summary>
/// Keeps registered factory entities in service after attacks: rebuilds destroyed ones at their recorded positions,
/// defenses first, reports registered electric entities off every powered network, then rearms registered turrets to
/// the deployment reserve. It only uses carried items; anything missing is reported as shortfall for the production
/// path, so a logistics round never starts a long production. Cells registered before plans were recorded recover
/// theirs from the native entities while all of them are still present.
/// </summary>
public sealed class FactoryMaintenance(IGameClient game, IControllerJournal journal, string directory)
{
    public async Task<MaintenanceResult> RunAsync(SpatialController controller, ProductionCatalog catalog, CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var snapshots = new FactorySnapshotClient(game);
        var rebuilt = new List<string>();
        var blocked = new List<string>();
        var supplied = new Dictionary<string, long>(StringComparer.Ordinal);
        var shortfall = new Dictionary<string, long>(StringComparer.Ordinal);
        int actions = 0;
        var snapshot = await CaptureAsync();
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        // Repair and rearming trips can prepare a survival kit; protect the latest registered input buffers.
        using var reservations = ProductionReservations.EnterFactory(state);
        // Rebuilding erases the evidence of an attack inside a goal; record it first for the between-goals response.
        await new AttackMonitor(directory, journal).RecordQuietlyAsync(state, snapshot, token);
        var recovered = RecoverPlans(state, snapshot, catalog);
        if (recovered.Count > 0)
        {
            state = recovered.Aggregate(state, (current, cell) => current.With(cell));
            await registry.SaveAsync(state, token);
            foreach (var cell in recovered) await journal.AppendAsync("factory-cell-plan-recovered", new { cell.Id, cell.Kind, cell.Plan }, token);
        }
        var carried = FactoryLogistics.Carried(snapshot);
        var registered = state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var zones = game is IDangerZoneReader reader
            ? await reader.ReadActiveDeathsAsync(catalog.Scope, 1, snapshot.CollectedTick, token) : [];
        foreach (var missing in Missing(state, Present(snapshot)))
        {
            // Full resource plans now permit maintenance to rebuild them before cell health reopens them.
            // Apply the same death-zone deferral as the resource builder before any travel or placement.
            if (DeferResourceRebuild(missing.Cell, zones))
            {
                blocked.Add(missing.PreviousId);
                await journal.AppendAsync("resource-cell-rebuild-deferred", new { cell = missing.Cell.Id, missing.Role, snapshot.CollectedTick }, token);
                continue;
            }
            string item = missing.Plan.Item;
            string? id = AtPlan(snapshot, catalog, missing.Plan, registered);
            bool adopted = id is not null;
            if (id is null && carried.GetValueOrDefault(item) < 1)
            {
                shortfall[item] = shortfall.GetValueOrDefault(item) + 1;
                await journal.AppendAsync("factory-rebuild-shortfall", new { cell = missing.Cell.Id, missing.Role, missing.PreviousId, item }, token);
                continue;
            }
            if (id is null)
            {
                var transportBus = missing.Cell.Kind == "transport" ? (state.Transports ?? []).SingleOrDefault(b => b.CellId == missing.Cell.Id) : null;
                if (missing.Cell.Kind == "transport" && transportBus is null)
                {
                    blocked.Add(missing.PreviousId);
                    await journal.AppendAsync("factory-transport-rebuild-unregistered", new { cell = missing.Cell.Id, missing.Role }, token);
                    continue;
                }
                string? stoppedItem = transportBus is not null && catalog.Items[item].PlaceEntityType == "inserter" ? transportBus.Item : null;
                try
                {
                    id = await new PoweredMachineController(game, journal).BuildAtAsync(item,
                        new(missing.Plan.Position, missing.Plan.Direction, 0, missing.Plan.UndergroundType), catalog, controller, token, stoppedInserterItem: stoppedItem);
                }
                catch (PlacementRefusedException error)
                {
                    // A refused placement changed nothing; the next round observes again rather than retrying blindly.
                    // Manual control, lease loss and receipt failures are not refusals and stop the round.
                    blocked.Add(missing.PreviousId);
                    await journal.AppendAsync("factory-rebuild-blocked", new { cell = missing.Cell.Id, missing.Role, missing.PreviousId, missing.Plan, error.Message }, token);
                    continue;
                }
                carried[item] = carried.GetValueOrDefault(item) - 1;
                actions++;
            }
            if (missing.Role == "machine" && FactoryCellBuilder.Configured(missing.Cell.Kind, missing.Cell.Recipe))
            {
                var configured = await controller.WorkAsync("set_recipe", new { entityId = id, recipe = missing.Cell.Recipe }, 600, token: token);
                actions++;
                if (configured.Status != "completed") throw new InvalidOperationException($"Rebuilt machine recipe ended with {configured.Status}.");
                actions += await new PoweredMachineController(game, journal).OrientFluidAsync(id, missing.Plan.Direction, catalog, controller, token);
            }
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var cell = current.Cells.Single(c => c.Id == missing.Cell.Id);
            await registry.SaveAsync(current.With(cell with
            {
                Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [missing.Role] = id }
            }), token);
            registered.Add(id);
            rebuilt.Add(id);
            await journal.AppendAsync("factory-rebuild", new { cell = cell.Id, cell.Kind, missing.Role, missing.PreviousId, entityId = id, adopted, missing.Plan }, token);
        }

        // Rebuilt turrets are rearmed in the same round.
        if (rebuilt.Count > 0) snapshot = await CaptureAsync();
        carried = FactoryLogistics.Carried(snapshot);
        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        // Every registered pole and machine, rebuilt or not, must reach a generator: one lost link pole silences whole cells.
        var unpowered = Unpowered(snapshot, state.Cells.Where(c => c.Status == "ready").SelectMany(c => c.Entities.Values));
        foreach (var id in unpowered)
            await journal.AppendAsync("factory-power-fault", new { entityId = id, rebuilt = rebuilt.Contains(id), snapshot.CollectedTick }, token);
        var turretIds = state.Cells.Where(c => c.Kind == "turret" && c.Status == "ready").SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        // Without registered turrets there is nothing to rearm; the defense reading is skipped.
        foreach (var turret in (turretIds.Count == 0 ? [] : DefenseFactoryState.Read(snapshot, catalog).Turrets).Where(t => t.Active && turretIds.Contains(t.Id))
            .OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var model = catalog.Turrets?.Values.FirstOrDefault(m => m.EntityName == turret.Name);
            if (model is null) continue;
            string ammunition;
            try { ammunition = turret.Ammunition ?? DefenseDeploymentPlanner.ChooseAmmunition(model, catalog, carried); }
            catch (InvalidOperationException error)
            {
                await journal.AppendAsync("factory-rearm-unavailable", new { turret.Id, error.Message }, token);
                continue;
            }
            long need = Magazines(turret.Rounds, catalog.Items[ammunition].MagazineSize!.Value);
            long give = Math.Min(need, carried.GetValueOrDefault(ammunition));
            if (give > 0)
            {
                long moved = await RearmAsync(turret, ammunition, give);
                carried[ammunition] = carried.GetValueOrDefault(ammunition) - moved;
                supplied[ammunition] = supplied.GetValueOrDefault(ammunition) + moved;
                need -= moved;
            }
            if (need > 0) shortfall[ammunition] = shortfall.GetValueOrDefault(ammunition) + need;
        }
        var result = new MaintenanceResult(rebuilt, blocked, supplied, shortfall, actions, snapshot.CollectedTick, unpowered,
            recovered.Select(c => c.Id).ToArray());
        await journal.AppendAsync("factory-maintenance", result, token);
        return result;

        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await snapshots.CaptureAsync(cancellationToken: token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during factory maintenance; reconcile rebuilds.");
            return value;
        }

        async Task<long> RearmAsync(InstalledTurret turret, string ammunition, long count)
        {
            await controller.ApproachEntityAsync(turret.Id, turret.Position, catalog, token);
            var receipt = await controller.WorkAsync("insert", new { entityId = turret.Id, inventory = "ammo", item = ammunition, count = checked((int)count) },
                600, token: token);
            actions++;
            if (receipt.Status is not ("completed" or "partial"))
            {
                await journal.AppendAsync("factory-transfer-refused", new { kind = "insert", turret.Id, ammunition, count, receipt.Status, receipt.Error }, token);
                return 0;
            }
            if (receipt.Effects.GetProperty("targetId").GetString() != turret.Id || receipt.Effects.GetProperty("item").GetString() != ammunition
                || receipt.Effects.GetProperty("inventory").GetString() != "ammo" || receipt.Effects.GetProperty("direction").GetString() != "from_actor")
                throw new InvalidDataException("Turret rearming lacks a matching native transfer receipt.");
            return receipt.Effects.GetProperty("transferred").GetInt64();
        }
    }

    internal static bool DeferResourceRebuild(FactoryCell cell, IReadOnlyList<NativeDeathTransition> zones) =>
        cell.IsResource && zones.Count > 0 && !ResourceCellBuilder.Safe(cell, zones);

    /// <summary>Reconciles interrupted construction or a manual replacement against known own entities before procurement.</summary>
    internal static FactoryCell Reconcile(FactoryCell cell, FactorySnapshot snapshot, ProductionCatalog catalog, IReadOnlySet<string> reserved,
        bool removeMissing = false)
    {
        var present = Present(snapshot);
        var ids = cell.Entities.Where(p => !removeMissing || present.Contains(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var used = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var (role, plan) in cell.Plan ?? new Dictionary<string, PlannedEntity>())
        {
            var excluded = new HashSet<string>(used, StringComparer.Ordinal);
            excluded.UnionWith(ids.Where(p => p.Key != role).Select(p => p.Value));
            string? id = AtPlan(snapshot, catalog, plan, excluded, allowBeltRotation: cell.Kind == "transport");
            if (id is null && removeMissing) ids.Remove(role);
            else if (id is not null) ids[role] = id;
        }
        return cell with { Entities = ids };
    }

    internal static string? AtPlan(FactorySnapshot snapshot, ProductionCatalog catalog, PlannedEntity plan, IReadOnlySet<string> excluded,
        bool allowBeltRotation = false)
    {
        string name = catalog.Items[plan.Item].PlaceEntity ?? plan.Item;
        return snapshot.Records.Where(r => r.Kind == "entity" && r.Name == name && !excluded.Contains(r.EntityId)
                && r.Data.GetProperty("role").GetString() == "factory"
                && r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(plan.Position) < .01
                && UndergroundType(r.Data) == plan.UndergroundType
                && (r.Data.GetProperty("type").GetString() is "container" or "electric-pole" or "wall" or "furnace"
                    || allowBeltRotation && r.Data.GetProperty("type").GetString() == "transport-belt"
                    || r.Data.GetProperty("direction").GetInt32() == plan.Direction))
            .Select(r => r.EntityId).FirstOrDefault();
    }

    /// <summary>Destroyed entities of ready cells with a recorded plan: turrets first, then walls, then production.</summary>
    public static IReadOnlyList<MissingEntity> Missing(FactoryState state, IReadOnlySet<string> present) => state.Cells
        .Where(c => c.Status == "ready" && c.Plan is not null)
        .SelectMany(c => c.Entities.Where(e => !present.Contains(e.Value) && c.Plan!.ContainsKey(e.Key))
            .Select(e => new MissingEntity(c, e.Key, e.Value, c.Plan![e.Key])))
        .OrderBy(m => m.Cell.Kind switch { "turret" => 0, "wall" => 1, _ => 2 })
        .ThenBy(m => m.Cell.Id, StringComparer.Ordinal).ThenBy(m => m.Role, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Plans for ready cells registered without one, read while every entity is present: the item placing the observed
    /// entity (the cell's machine item first) and its native position and direction.
    /// </summary>
    public static IReadOnlyList<FactoryCell> RecoverPlans(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog)
    {
        var entities = snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory")
            .ToDictionary(r => r.EntityId, StringComparer.Ordinal);
        var recovered = new List<FactoryCell>();
        foreach (var cell in state.Cells.Where(c => c.Status == "ready" && c.Plan is null && c.Entities.Count > 0 && c.Entities.Values.All(entities.ContainsKey)))
        {
            var plan = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal);
            foreach (var (role, id) in cell.Entities)
            {
                var record = entities[id];
                string? item = catalog.Items.Where(p => p.Value.PlaceEntity == record.Name).Select(p => p.Key)
                    .OrderBy(k => k != cell.MachineItem).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
                if (item is null) break;
                string? underground = UndergroundType(record.Data);
                if (record.Data.GetProperty("type").GetString() == "underground-belt" && underground is null) break;
                plan[role] = new(role, item, record.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!,
                    record.Data.GetProperty("direction").GetInt32(), underground);
            }
            if (plan.Count == cell.Entities.Count) recovered.Add(cell with { Plan = plan });
        }
        return recovered;
    }

    private static string? UndergroundType(JsonElement data) => data.TryGetProperty("type", out var type)
        && type.GetString() == "underground-belt" && data.TryGetProperty("transport", out var transport)
        && transport.TryGetProperty("underground", out var underground) && underground.TryGetProperty("type", out var value)
        && value.GetString() is "input" or "output" ? value.GetString() : null;

    /// <summary>Electric entities among the given ones whose native network holds no known power source.</summary>
    public static IReadOnlyList<string> Unpowered(FactorySnapshot snapshot, IEnumerable<string> ids)
    {
        var entities = snapshot.Records.Where(r => r.Kind == "entity").ToDictionary(r => r.EntityId, StringComparer.Ordinal);
        static bool Electric(FactoryRecord record) => record.Data.TryGetProperty("power", out var power) && power.ValueKind == JsonValueKind.Object;
        static long? Network(FactoryRecord record) => record.Data.GetProperty("power").TryGetProperty("networkId", out var id)
            && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null;
        var powered = entities.Values.Where(r => Electric(r) && FactoryCellBuilder.IsPowerSource(r.Data.GetProperty("type").GetString()!))
            .Select(Network).OfType<long>().ToHashSet();
        return ids.Where(id => entities.TryGetValue(id, out var record) && Electric(record) && !(Network(record) is { } network && powered.Contains(network)))
            .ToArray();
    }

    /// <summary>Ready cells with registered entities that are gone; the item is known only when the cell recorded a plan.</summary>
    public static IReadOnlyList<DegradedCell> Degraded(IEnumerable<FactoryCell> cells, IReadOnlySet<string> present) => cells
        .Where(c => c.Status == "ready")
        .Select(c => new DegradedCell(c.Id, c.Entities.Where(e => !present.Contains(e.Value))
            .Select(e => new DegradedEntity(e.Key, e.Value, c.Plan?.GetValueOrDefault(e.Key)?.Item)).ToArray()))
        .Where(d => d.Missing.Count > 0).ToArray();

    /// <summary>Whole magazines that restore the deployment reserve, measured in remaining rounds.</summary>
    public static int Magazines(long rounds, int magazineSize) =>
        checked((int)Math.Ceiling(Math.Max(0, DefenseDeploymentPlanner.ReserveRounds - rounds) / (double)magazineSize));

    public static IReadOnlySet<string> Present(FactorySnapshot snapshot) =>
        snapshot.Records.Where(r => r.Kind == "entity").Select(r => r.EntityId).ToHashSet(StringComparer.Ordinal);
}
