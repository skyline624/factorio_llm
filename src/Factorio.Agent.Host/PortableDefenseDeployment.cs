using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Proposes paid local defense actions to the existing arbiter; never executes an action itself.</summary>
internal sealed class PortableDefenseDeployment(IGameClient game, IControllerJournal journal, FactoryRegistry? registry)
{
    private ProductionCatalog? catalog;
    private PortableDefensePlan? plan;
    private ActorScope? scope;
    private string? entityId;
    private OperationSubmission? proposed;
    private bool loaded;
    private long refusedUntil;
    private readonly List<MapPosition> refusedPlacements = [];
    private const int MaximumPlacementRefusals = 3;
    public bool ObservationChanged { get; private set; }

    public async Task<OperationSubmission?> NextAsync(SafetyObservation observed, CancellationToken token,
        MapPosition? protectedDestination = null)
    {
        ObservationChanged = false;
        if (!observed.Alive || observed.ControlMode != "ai" || observed.StopUnconfirmed || observed.Position is null
            || observed.Health <= 0 || observed.Inventory is null) return null;
        if (scope is not null && scope != observed.Scope)
        {
            // A new incarnation re-observes physical defenses; it cannot load an old proposal's target.
            plan = null; entityId = null; proposed = null; loaded = false; catalog = null; refusedUntil = 0;
            refusedPlacements.Clear();
        }
        if (refusedUntil > 0 && observed.Tick >= refusedUntil) { refusedUntil = 0; refusedPlacements.Clear(); }
        if (loaded && entityId is not null)
        {
            bool ready = observed.Defenses?.Any(t => t.Id == entityId && observed.Position.DistanceTo(t.Position) <= RetreatPlanner.CoverRadius(t)) == true;
            await journal.AppendAsync("portable-defense-coverage", new { entityId, ready, observed.Scope, observed.Tick }, token);
            plan = null; entityId = null; proposed = null; loaded = false;
            // A matching paid transfer is not a claim of loaded coverage: only the fresh native frame is.
        }
        bool pack = observed.LocalEnemiesComplete && observed.Enemies.Count(e => e.Type == "unit") >= RetreatPlanner.OutnumberedEnemies;
        if (!pack && entityId is null) refusedPlacements.Clear();
        if (entityId is null && (!pack || observed.Tick < refusedUntil
            || observed.Defenses?.Count(t => observed.Position.DistanceTo(t.Position) <= RetreatPlanner.CoverRadius(t)) >= PortableDefensePlanner.TurretReserve)) return null;
        catalog ??= ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (catalog.Scope != observed.Scope) throw new InvalidDataException("Portable defense catalog belongs to another actor.");
        if (entityId is null && catalog.Turrets?.Any(t => observed.Inventory.GetValueOrDefault(t.Key) > 0) != true) return null;
        var map = await new SpatialClient(game).CaptureAsync(catalog.Turrets?.Keys.ToArray(), cancellationToken: token);
        if (map.Scope != observed.Scope) throw new InvalidDataException("Portable defense terrain belongs to another actor.");
        if (map.Actor.ControlMode != "ai" || map.CollectedTick < observed.Tick || map.CollectedTick - observed.Tick > 60
            || map.Actor.Position.DistanceTo(observed.Position) > .5)
        { ObservationChanged = true; return null; }
        if (entityId is not null && plan is not null)
        {
            var target = map.Entities.SingleOrDefault(e => e.Id == entityId);
            if (target is null || target.Name != catalog.Turrets![plan.Turret].EntityName
                || target.Position.DistanceTo(plan.Placement.Position) > .01
                || target.Position.DistanceTo(map.Actor.Position) > map.Actor.ReachDistance - 1
                || observed.Inventory.GetValueOrDefault(plan.Ammunition) < plan.Magazines + SurvivalKitPlanner.MagazineReserve)
            {
                await journal.AppendAsync("portable-defense-load-deferred", new { entityId, observed.Tick }, token);
                plan = null; entityId = null; proposed = null; refusedUntil = observed.Tick + 600;
                return null;
            }
            proposed = OperationSubmission.Create(map.Scope, "insert", new { entityId, inventory = "ammo", item = plan.Ammunition, count = plan.Magazines },
                map.CollectedTick + 180, new { position = map.Actor.Position, positionTolerance = .5 });
            return proposed;
        }
        plan = new PortableDefensePlanner().Find(observed, map, catalog, observed.Inventory, token,
            requiredCovers: PortableDefensePlanner.TurretReserve, refusedPlacements: refusedPlacements,
            protectedDestination: protectedDestination);
        if (plan is null) return null;
        scope = map.Scope;
        proposed = OperationSubmission.Create(map.Scope, "build", new { item = plan.Turret, plan.Placement.Position, plan.Placement.Direction },
            map.CollectedTick + 180, new { position = map.Actor.Position, positionTolerance = .5 });
        await journal.AppendAsync("portable-defense-plan", new { map.Scope, map.CollectedTick, plan }, token);
        return proposed;
    }

    public async Task AcceptAsync(OperationSubmission submission, OperationReceipt receipt, CancellationToken token)
    {
        if (submission.OperationId != proposed?.OperationId || !receipt.IsTerminal) return;
        if (receipt.OperationId != submission.OperationId || receipt.Kind != submission.Kind)
            throw new InvalidDataException("Portable defense receipt does not match its submitted action.");
        if (receipt.Status is not ("completed" or "partial"))
        {
            // A known empty placement refusal may select a different position after another native capture.
            // Unknown outcomes and any partial mutation evidence can never be discarded or retransmitted.
            if (receipt.Effects.TryGetProperty("entityId", out _) || receipt.Effects.TryGetProperty("consumed", out _)
                || receipt.Effects.TryGetProperty("transferred", out var transfer) && transfer.GetInt64() > 0
                || receipt.Effects.TryGetProperty("inventoryDelta", out var delta)
                    && (delta.ValueKind != JsonValueKind.Object || delta.EnumerateObject().Any()))
                throw new InvalidDataException("Portable defense failed after a possible native mutation; reconcile its effects.");
            await journal.AppendAsync("portable-defense-refused", receipt, token);
            bool emptyPlacementRefusal = submission.Kind == "build" && receipt.Status == "failed"
                && receipt.Error?.Code == "placement_blocked" && plan is not null
                && receipt.Effects.TryGetProperty("inventoryDelta", out var inventoryDelta)
                && inventoryDelta.ValueKind == JsonValueKind.Object && !inventoryDelta.EnumerateObject().Any();
            if (emptyPlacementRefusal) refusedPlacements.Add(plan!.Placement.Position);
            refusedUntil = emptyPlacementRefusal && refusedPlacements.Count < MaximumPlacementRefusals ? 0 : receipt.UpdatedTick + 600;
            plan = null; entityId = null; proposed = null;
            return;
        }
        try
        {
            if (submission.Kind == "build")
            {
                var effects = receipt.Effects;
                string id = effects.GetProperty("entityId").GetString() ?? "";
                var position = effects.GetProperty("entityPosition").Deserialize<MapPosition>(Protocol.Json);
                var consumed = effects.GetProperty("consumed");
                if (receipt.Status != "completed" || string.IsNullOrWhiteSpace(id) || position is null
                    || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.DistanceTo(plan!.Placement.Position) > .01
                    || effects.GetProperty("entityName").GetString() != catalog!.Turrets![plan.Turret].EntityName
                    || consumed.EnumerateObject().Count() != 1 || consumed.GetProperty(plan.Turret).GetInt64() != 1)
                    throw new InvalidDataException("Portable turret construction lacks matching native placement and cost evidence.");
                entityId = id;
                if (registry is not null)
                {
                    var cell = new FactoryCell("portable-defense-" + id, 0, new(0, 0, true), "turret", plan.Turret, null,
                        new Dictionary<string, string> { ["turret"] = id }, "ready", receipt.UpdatedTick,
                        Plan: new Dictionary<string, PlannedEntity> { ["turret"] = new("turret", plan.Turret, position, plan.Placement.Direction) });
                    await registry.SaveAsync((await registry.LoadAsync(submission.Scope.WorldId, token)).With(cell), token);
                    await journal.AppendAsync("portable-defense-cell", cell, token);
                }
            }
            else
            {
                long moved = FactoryMaintenance.TransferredAmmunition(receipt, entityId!, plan!.Ammunition, plan.Magazines);
                if (receipt.Effects.GetProperty("requested").GetInt32() != plan.Magazines
                    || receipt.Status == "completed" && moved != plan.Magazines)
                    throw new InvalidDataException("Portable turret loading lacks matching paid magazine evidence.");
                loaded = true;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        { throw new InvalidDataException("Invalid native portable defense receipt.", error); }
    }
}
