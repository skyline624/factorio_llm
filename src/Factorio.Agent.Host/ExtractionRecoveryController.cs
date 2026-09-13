using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Recovers only owned drills whose complete native mining area has no remaining compatible resource.</summary>
internal sealed class ExtractionRecoveryController(IGameClient game, IControllerJournal journal)
{
    public async Task<bool> RecoverNearbyAsync(IReadOnlyList<string> drillItems, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        string[] items = drillItems.Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length is 0 or > 16) return false;
        var state = await new ProductionController(game, journal).ObserveAsync(token);
        if (state.Scope != catalog.Scope || state.ControlMode != "ai") throw new InvalidDataException("Extraction recovery actor changed.");
        if (items.Any(item => state.Inventory.GetValueOrDefault(item) > 0)) return false;
        var map = await new SpatialClient(game).CaptureAsync(items, 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Extraction recovery map changed.");
        var names = items.Select(item => catalog.Items[item].PlaceEntity).ToHashSet(StringComparer.Ordinal);
        foreach (var drill in map.Entities.Where(e => names.Contains(e.Name) && !ProductionReservations.Current.Contains(e.Id)
                     && state.Entities.Any(owned => owned.Id == e.Id) && map.Prototypes[e.Name].FuelCategories is { Count: > 0 })
                 .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)))
            if (!ExtractionPlanner.HasRemainingResources(map, drill)
                && await TryRecoverAsync(drill.Id, catalog, controller, token)) return true;
        return false;
    }

    public async Task<bool> TryRecoverAsync(string drillId, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (ProductionReservations.Current.Contains(drillId)) throw new InvalidOperationException("The drill is reserved by another production stage.");
        var production = new ProductionController(game, journal);
        var state = await production.ObserveAsync(token);
        RequireScope(state.Scope);
        if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        var owned = state.Entities.Single(e => e.Id == drillId);
        string item = catalog.Items.Where(p => p.Value.PlaceEntity == owned.Name && p.Value.PlaceEntityType == "mining-drill")
            .OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var spatial = new SpatialClient(game);
        var map = await spatial.CaptureAsync([item], 48, token);
        RequireScope(map.Scope);
        var drill = map.Entities.SingleOrDefault(e => e.Id == drillId);
        // Absence from a local view cannot prove depletion of a distant drill.
        if (drill is null || ExtractionPlanner.HasRemainingResources(map, drill)) return false;
        await controller.ApproachEntityAsync(drillId, owned.Position, catalog, token);
        map = await spatial.CaptureAsync([item], 48, token); RequireScope(map.Scope);
        drill = map.Entities.Single(e => e.Id == drillId);
        if (ExtractionPlanner.HasRemainingResources(map, drill)) return false;
        state = await production.ObserveAsync(token); RequireScope(state.Scope);
        // Empty the fuel inventory through checked transfers before recovering the machine itself.
        foreach (var fuel in state.Entities.Single(e => e.Id == drillId).Items("fuel"))
        {
            if (fuel.Value <= 0) continue;
            var taken = await controller.WorkAsync("take", new { entityId = drillId, inventory = "fuel", item = fuel.Key, count = fuel.Value }, 600, token: token);
            RequireCompleted(taken);
        }
        var recovered = await controller.WorkAsync("mine", new { entityId = drillId, count = 1 }, 36000, token: token);
        RequireCompleted(recovered);
        var after = await production.ObserveAsync(token); RequireScope(after.Scope);
        if (after.Entities.Any(e => e.Id == drillId) || after.Inventory.GetValueOrDefault(item) <= state.Inventory.GetValueOrDefault(item))
            throw new InvalidDataException("Native recovery did not remove the drill and return its construction item.");
        await journal.AppendAsync("depleted-extractor-recovered", new { drillId, item, map.Scope, map.CollectedTick,
            recovered.OperationId, recovered.Effects, after.Tick,
            interpretation = "Complete mining area empty; stored fuel transferred first. Native recovery may discard energy of fuel already burning." }, token);
        return true;

        void RequireScope(ActorScope scope) { if (scope != catalog.Scope) throw new InvalidDataException("Extraction recovery scope changed."); }
        static void RequireCompleted(OperationReceipt receipt)
        {
            if (receipt.Status != "completed") throw new InvalidOperationException($"Extractor recovery ended with {receipt.Status}: {receipt.Error?.Code}; reconcile partial effects.");
        }
    }
}
