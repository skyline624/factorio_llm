using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Bootstraps only the equipment needed for machine extraction, then leaves bulk production to the machines.</summary>
internal sealed class SmeltingPreparationController(IGameClient game, IControllerJournal journal)
{
    private static readonly AsyncLocal<bool> Preparing = new();
    internal static bool IsPreparing => Preparing.Value;

    public async Task PrepareAsync(string item, CancellationToken token)
    {
        if (Preparing.Value) throw new InvalidOperationException("Recursive extraction installation is not a bootstrap route.");
        await BootstrapAsync(() => PrepareCoreAsync(item, token));
    }

    internal static async Task BootstrapAsync(Func<Task> action)
    {
        bool previous = Preparing.Value;
        Preparing.Value = true;
        try { await action(); }
        finally { Preparing.Value = previous; }
    }

    private async Task PrepareCoreAsync(string item, CancellationToken token)
    {
        var production = new ProductionController(game, journal);
        var state = await production.ObserveAsync(token);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (catalog.Scope != state.Scope) throw new InvalidDataException("Extraction preparation scope changed.");
        var planner = new SmeltingPreparationPlanner();
        var options = planner.Options(catalog, state.Inventory, item);
        if (options.Count == 0) throw new InvalidOperationException("No obtainable native extraction equipment for this material.");
        string[] equipment = options.SelectMany(p => new[] { p.DrillItem, p.FurnaceItem }).Distinct(StringComparer.Ordinal).ToArray();
        if (equipment.Length > 16) throw new InvalidOperationException("Extraction equipment exceeds the native geometry budget.");
        var spatial = new SpatialClient(game);
        await using var controller = new SpatialController(game, journal);
        await new ExtractionRecoveryController(game, journal).RecoverNearbyAsync(options.Select(p => p.DrillItem).ToArray(), catalog, controller, token);
        state = await ObserveAsync();
        await journal.AppendAsync("smelting-preparation-start", new { item, state.Scope, state.Tick, options,
            manualMiningPolicy = "Only equipment bootstrap ingredients; bulk ore must come from the prepared machines." }, token);

        // Known distant machines are inspected before commissioning another site; registry positions are travel hints.
        var known = state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)
            && options.Any(p => catalog.Machines[p.FurnaceItem].EntityName == e.Name && e.AsMachine().CanProcess(p.Recipe))).ToArray();
        var initialMap = await MapAsync();
        foreach (var receiver in known.OrderBy(e => e.Position.DistanceTo(initialMap.Actor.Position)).Take(8))
        {
            await controller.TravelAsync(receiver.Position, 8, catalog, token);
            await new ExtractionRecoveryController(game, journal).RecoverNearbyAsync(options.Select(p => p.DrillItem).ToArray(), catalog, controller, token);
            var map = await MapAsync();
            state = await ObserveAsync();
            var constructible = options.Select(p => p.DrillItem).ToHashSet(StringComparer.Ordinal);
            var plan = new SmeltingPlanner().Find(item, catalog, map, state.Inventory, Known(state), constructible);
            if (plan is null) continue;
            await journal.AppendAsync("smelting-preparation-existing-site", new { plan, map.CollectedTick }, token);
            if (plan.ExistingDrillId is null) await production.ProduceAsync(plan.DrillItem, 1, token);
            await controller.TravelAsync(map.Entities.Single(e => e.Id == plan.Connection.ReceiverId).Position, 8, catalog, token);
            map = await MapAsync();
            state = await ObserveAsync();
            if (new SmeltingPlanner().Find(item, catalog, map, state.Inventory, Known(state)) is null)
                throw new InvalidOperationException("The existing extraction site changed while preparing equipment; reconcile before construction.");
            return;
        }

        var exploration = new ExplorationPlanner();
        for (int attempt = 0; attempt < 64; attempt++)
        {
            var map = await MapAsync();
            state = await ObserveAsync();
            var site = await ControllerPlanning.RunAsync(cancellation => planner.Find(map, catalog, state.Inventory, item, cancellation),
                controller, TimeSpan.FromMinutes(5), token);
            if (site is null)
            {
                string ore = options[0].Recipe.Ingredients[0].Name;
                var next = await controller.FindExplorationWaypointAsync(exploration, catalog, ore, token: token);
                await journal.AppendAsync("smelting-preparation-exploration", new { item, ore, next }, token);
                await controller.NavigateAsync(next.Position, cancellationToken: token);
                continue;
            }
            await journal.AppendAsync("smelting-site-plan", new { site, map.CollectedTick }, token);
            // A drill recipe may consume a furnace; obtain the receiving furnace last.
            await production.ProduceAsync(site.Equipment.DrillItem, 1, token);
            await production.ProduceAsync(site.Equipment.FurnaceItem, 1, token);
            await controller.TravelAsync(site.Furnace.Position, 8, catalog, token);
            map = await MapAsync();
            state = await ObserveAsync();
            site = await ControllerPlanning.RunAsync(cancellation => planner.Find(map, catalog, state.Inventory, item, cancellation),
                controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException("The extraction construction area changed during equipment preparation.");
            if (state.Inventory.GetValueOrDefault(site.Equipment.DrillItem) < 1 || state.Inventory.GetValueOrDefault(site.Equipment.FurnaceItem) < 1)
                throw new InvalidOperationException("The replanned extraction equipment is not in the actor inventory.");
            string furnaceId = await new PoweredMachineController(game, journal).BuildAtAsync(site.Equipment.FurnaceItem,
                site.Furnace, catalog, controller, token, [site.Connection.Drill.Position]);
            await journal.AppendAsync("smelting-site-furnace-built", new { furnaceId, site }, token);
            map = await MapAsync();
            state = await ObserveAsync();
            var ready = new SmeltingPlanner().Find(item, catalog, map, state.Inventory, Known(state));
            if (ready is null || ready.Connection.ReceiverId != furnaceId)
                throw new InvalidOperationException("The built furnace has no confirmed extraction connection; preserve the partial installation.");
            return;
        }
        throw new TimeoutException("Extraction preparation exhausted its observed-site exploration budget.");

        Dictionary<string, KnownProductionMachine> Known(ProductionState value) => value.Entities
            .Where(e => !ProductionReservations.Current.Contains(e.Id)).ToDictionary(e => e.Id, e => e.AsMachine(), StringComparer.Ordinal);
        async Task<ProductionState> ObserveAsync()
        {
            var value = await production.ObserveAsync(token);
            if (value.Scope != catalog.Scope || value.ControlMode != "ai") throw new InvalidDataException("Extraction preparation actor changed.");
            return value;
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync(equipment, 48, token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Extraction preparation map scope changed.");
            return value;
        }
    }
}
