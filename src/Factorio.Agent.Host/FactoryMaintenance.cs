using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record MissingEntity(FactoryCell Cell, string Role, string PreviousId, PlannedEntity Plan);
public sealed record MaintenanceResult(IReadOnlyList<string> Rebuilt, IReadOnlyList<string> Blocked,
    IReadOnlyDictionary<string, long> Supplied, IReadOnlyDictionary<string, long> Shortfall, int Actions, long Tick);

/// <summary>
/// Keeps registered factory entities in service after attacks: rebuilds destroyed ones at their recorded positions,
/// defenses first, then rearms registered turrets to the deployment reserve. It only uses carried items; anything
/// missing is reported as shortfall for the production path, so a logistics round never starts a long production.
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
        var carried = FactoryLogistics.Carried(snapshot);
        var registered = state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in Missing(state, Present(snapshot)))
        {
            string item = missing.Plan.Item;
            string? id = Adopt(missing.Plan);
            bool adopted = id is not null;
            if (id is null && carried.GetValueOrDefault(item) < 1)
            {
                shortfall[item] = shortfall.GetValueOrDefault(item) + 1;
                await journal.AppendAsync("factory-rebuild-shortfall", new { cell = missing.Cell.Id, missing.Role, missing.PreviousId, item }, token);
                continue;
            }
            if (id is null)
            {
                try
                {
                    id = await new PoweredMachineController(game, journal).BuildAtAsync(item,
                        new(missing.Plan.Position, missing.Plan.Direction, 0), catalog, controller, token);
                }
                catch (InvalidOperationException error)
                {
                    // A refused placement changed nothing; the next round observes again rather than retrying blindly.
                    blocked.Add(missing.PreviousId);
                    await journal.AppendAsync("factory-rebuild-blocked", new { cell = missing.Cell.Id, missing.Role, missing.PreviousId, missing.Plan, error.Message }, token);
                    continue;
                }
                carried[item] = carried.GetValueOrDefault(item) - 1;
                actions++;
            }
            if (missing.Role == "machine" && missing.Cell.Recipe is not null)
            {
                var configured = await controller.WorkAsync("set_recipe", new { entityId = id, recipe = missing.Cell.Recipe }, 600, token: token);
                actions++;
                if (configured.Status != "completed") throw new InvalidOperationException($"Rebuilt machine recipe ended with {configured.Status}.");
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
        var turretIds = state.Cells.Where(c => c.Kind == "turret" && c.Status == "ready").SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        foreach (var turret in DefenseFactoryState.Read(snapshot, catalog).Turrets.Where(t => t.Active && turretIds.Contains(t.Id))
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
        var result = new MaintenanceResult(rebuilt, blocked, supplied, shortfall, actions, snapshot.CollectedTick);
        await journal.AppendAsync("factory-maintenance", result, token);
        return result;

        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await snapshots.CaptureAsync(cancellationToken: token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during factory maintenance; reconcile rebuilds.");
            return value;
        }

        // An entity built before an interrupted receipt reached the registry is found where the plan expects it.
        string? Adopt(PlannedEntity plan)
        {
            string name = catalog.Items[plan.Item].PlaceEntity ?? plan.Item;
            return snapshot.Records.Where(r => r.Kind == "entity" && r.Name == name && !registered.Contains(r.EntityId)
                    && r.Data.GetProperty("role").GetString() == "factory"
                    && r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(plan.Position) < .01
                    && (r.Data.GetProperty("type").GetString() is "container" or "electric-pole" or "wall"
                        || r.Data.GetProperty("direction").GetInt32() == plan.Direction))
                .Select(r => r.EntityId).FirstOrDefault();
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

    /// <summary>Destroyed entities of ready cells with a recorded plan: turrets first, then walls, then production.</summary>
    public static IReadOnlyList<MissingEntity> Missing(FactoryState state, IReadOnlySet<string> present) => state.Cells
        .Where(c => c.Status == "ready" && c.Plan is not null)
        .SelectMany(c => c.Entities.Where(e => !present.Contains(e.Value) && c.Plan!.ContainsKey(e.Key))
            .Select(e => new MissingEntity(c, e.Key, e.Value, c.Plan![e.Key])))
        .OrderBy(m => m.Cell.Kind switch { "turret" => 0, "wall" => 1, _ => 2 })
        .ThenBy(m => m.Cell.Id, StringComparer.Ordinal).ThenBy(m => m.Role, StringComparer.Ordinal).ToArray();

    /// <summary>Whole magazines that restore the deployment reserve, measured in remaining rounds.</summary>
    public static int Magazines(long rounds, int magazineSize) =>
        checked((int)Math.Ceiling(Math.Max(0, DefenseDeploymentPlanner.ReserveRounds - rounds) / (double)magazineSize));

    public static IReadOnlySet<string> Present(FactorySnapshot snapshot) =>
        snapshot.Records.Where(r => r.Kind == "entity").Select(r => r.EntityId).ToHashSet(StringComparer.Ordinal);
}
