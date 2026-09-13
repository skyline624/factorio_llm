using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record StoredResourceExtractionResult(string Item, int TargetStock, long InitialStock, long FinalStock,
    string DrillId, string ChestId, long StartTick, long EndTick);

/// <summary>Builds or reuses a drill-to-storage connection and proves net delivery after native fuel consumption.</summary>
internal sealed class StoredResourceExtractionController(IGameClient game, IControllerJournal journal)
{
    private static readonly AsyncLocal<bool> Preparing = new();
    internal static bool IsPreparing => Preparing.Value;

    public async Task<StoredResourceExtractionResult> RunAsync(string item, int targetStock, CancellationToken token)
    {
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        if (Preparing.Value) throw new InvalidOperationException("Recursive raw extraction bootstrap is not allowed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var production = new ProductionController(game, journal);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var spatial = new SpatialClient(game);
        var planner = new StoredResourceExtractionPlanner();
        await using var controller = new SpatialController(game, journal);
        ProductionState initial = await ObserveAsync();
        string[] equipment = planner.Options(catalog, initial.Inventory, item).SelectMany(p => new[] { p.DrillItem, p.ChestItem })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (equipment.Length is 0 or > 16) throw new InvalidOperationException("No supported bounded native drill/storage equipment catalog.");
        await journal.AppendAsync("stored-extraction-start", new { item, targetStock, initial.Tick, initial.Scope }, token);
        var map = await MapAsync();
        ResourceExtractionPlan plan = await PrepareAsync();
        map = await MapAsync();
        var drill = map.Entities.Single(e => e.Id == plan.ExistingDrillId);
        var chest = map.Entities.Single(e => e.Id == plan.Connection.ReceiverId);
        await journal.AppendAsync("stored-extraction-connection", new { item, drillId = drill.Id, chestId = chest.Id, plan, map.CollectedTick }, token);

        for (int iteration = 0; iteration < 1800; iteration++)
        {
            var state = await ObserveAsync();
            long carried = state.Inventory.GetValueOrDefault(item);
            if (carried >= targetStock)
            {
                var result = new StoredResourceExtractionResult(item, targetStock, initial.Inventory.GetValueOrDefault(item), carried,
                    drill.Id, chest.Id, initial.Tick, state.Tick);
                await journal.AppendAsync("stored-extraction-result", result, token);
                return result;
            }
            long output = state.Entities.Single(e => e.Id == chest.Id).Count("output", item);
            if (output > 0)
            {
                await controller.ApproachEntityAsync(chest.Id, chest.Position, catalog, token);
                await WorkAsync("take", new { entityId = chest.Id, inventory = "chest", item, count = Math.Min(output, targetStock - carried) });
                continue;
            }
            var stock = await new FactorySnapshotClient(game).CaptureAsync([item], cancellationToken: token);
            RequireScope(stock.Scope);
            var supplied = StoredExtractionStock.From(stock, drill.Id, chest.Id, item);
            if (supplied.Output > 0) continue;
            if (supplied.Insertable == 0) throw new InvalidOperationException("Native storage capacity blocks extraction; reconcile its bar, filters or contents.");
            if (iteration % 10 == 0 && await new ExtractionRecoveryController(game, journal).TryRecoverAsync(drill.Id, catalog, controller, token))
            {
                plan = await PrepareAsync();
                map = await MapAsync();
                drill = map.Entities.Single(e => e.Id == plan.ExistingDrillId);
                chest = map.Entities.Single(e => e.Id == plan.Connection.ReceiverId);
                await journal.AppendAsync("stored-extraction-connection", new { item, drillId = drill.Id, chestId = chest.Id, plan, map.CollectedTick }, token);
                continue;
            }
            if (supplied.StoredFuel == 0 && supplied.BurningJoules == 0) await FuelAsync(state);
            await WorkAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Stored extraction exhausted its observation budget; reconcile the preserved installation.");

        async Task<ResourceExtractionPlan> PrepareAsync()
        {
            await new ExtractionRecoveryController(game, journal).RecoverNearbyAsync(
                planner.Options(catalog, (await ObserveAsync()).Inventory, item).Select(p => p.DrillItem).ToArray(), catalog, controller, token);
            var state = await ObserveAsync();
            ResourceExtractionPlan? selected = null;
            // Registry locations are hints only; native geometry is recaptured at the destination.
            foreach (var known in state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)
                         && catalog.Items.Values.Any(i => i.PlaceEntity == e.Name && i.PlaceEntityType == "container")
                         && e.AsMachine().Output!.All(p => p.Value == 0 || p.Key == item))
                     .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).Take(8))
            {
                await controller.TravelAsync(known.Position, 8, catalog, token);
                map = await MapAsync(); state = await ObserveAsync();
                selected = await PlanAsync(state);
                if (selected is { NewChest: null }) break;
                selected = null;
            }
            var exploration = new ExplorationPlanner();
            for (int attempt = 0; selected is null && attempt < 64; attempt++)
            {
                map = await MapAsync(); state = await ObserveAsync();
                selected = await PlanAsync(state);
                if (selected is not null) break;
                var next = await controller.FindExplorationWaypointAsync(exploration, catalog, item, token: token);
                await journal.AppendAsync("stored-extraction-exploration", new { item, next }, token);
                await controller.NavigateAsync(next.Position, cancellationToken: token);
            }
            if (selected is null) throw new TimeoutException("No observed resource supports a drill-to-storage site within the exploration budget.");
            if (selected.ExistingDrillId is not null) return selected;
            await journal.AppendAsync("stored-extraction-site", new { item, selected, map.CollectedTick }, token);
            await BootstrapAsync(selected.Equipment.DrillItem, 1);
            if (selected.NewChest is not null) await BootstrapAsync(selected.Equipment.ChestItem, 1);
            await controller.TravelAsync(selected.Connection.Drill.Position, 8, catalog, token);
            map = await MapAsync(); state = await ObserveAsync();
            selected = await PlanAsync(state) ?? throw new InvalidOperationException("Resource site changed while preparing equipment.");
            if (selected.ExistingDrillId is not null) return selected;
            if (selected.NewChest is not null)
            {
                await new PoweredMachineController(game, journal).BuildAtAsync(selected.Equipment.ChestItem, selected.NewChest,
                    catalog, controller, token, [selected.Connection.Drill.Position]);
                map = await MapAsync(); state = await ObserveAsync();
                selected = await PlanAsync(state) ?? throw new InvalidOperationException("The built chest has no usable extraction connection.");
                if (selected.NewChest is not null) throw new InvalidOperationException("The partial chest installation requires reconciliation.");
            }
            string drillId = await new PoweredMachineController(game, journal).BuildAtAsync(selected.Equipment.DrillItem,
                selected.Connection.Drill, catalog, controller, token);
            map = await MapAsync(); state = await ObserveAsync();
            var confirmed = planner.Find(item, catalog, map, state.Inventory, Owned(state), allowConstruction: false);
            if (confirmed?.ExistingDrillId != drillId) throw new InvalidDataException("Native drill drop does not confirm the installed storage connection.");
            return confirmed;
        }

        async Task FuelAsync(ProductionState state)
        {
            map = await MapAsync();
            var confirmed = planner.Find(item, catalog, map, state.Inventory, Owned(state), allowConstruction: false);
            if (confirmed?.ExistingDrillId != drill.Id || confirmed.Connection.ReceiverId != chest.Id)
                throw new InvalidOperationException("The extraction connection or remaining resource changed; reconcile before refuelling.");
            var categories = map.Prototypes[drill.Name].FuelCategories!;
            var fuels = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category && categories.ContainsKey(category))
                .OrderByDescending(p => state.Inventory.GetValueOrDefault(p.Key) > 0)
                .ThenByDescending(p => state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)).Sum(e => e.Count("output", p.Key)) > 0)
                .ThenByDescending(p => p.Value.FuelValue).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
            var chosen = fuels.FirstOrDefault(p => state.Inventory.GetValueOrDefault(p.Key) > 0
                || state.Entities.Any(e => !ProductionReservations.Current.Contains(e.Id) && e.Count("output", p.Key) > 0)
                || catalog.Mining.Values.Any(products => products.Any(p2 => p2.Name == p.Key && p2.DeterministicItem)));
            if (chosen.Key is null) throw new InvalidOperationException("No obtainable native burner fuel.");
            double work = ExtractionPlanner.WorkEnergy(confirmed.Connection, map, plan.Equipment.DrillItem, catalog, item,
                Math.Max(1, targetStock - state.Inventory.GetValueOrDefault(item)));
            int reserve = checked((int)Math.Clamp(Math.Ceiling(work * 1.25 / chosen.Value.FuelValue), 1, chosen.Value.StackSize));
            long available = state.Inventory.GetValueOrDefault(chosen.Key);
            if (available == 0)
            {
                long stored = state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)).Sum(e => e.Count("output", chosen.Key));
                // A cold burner needs one starter item; its own coal can then sustain further extraction.
                await BootstrapAsync(chosen.Key, stored > 0 ? checked((int)Math.Min(reserve, stored)) : 1);
            }
            await controller.ApproachEntityAsync(drill.Id, drill.Position, catalog, token);
            var stock = await new FactorySnapshotClient(game).CaptureAsync([chosen.Key], cancellationToken: token);
            RequireScope(stock.Scope);
            string inventoryId = stock.Records.Single(r => r.Kind == "entity" && r.EntityId == drill.Id).Data.GetProperty("fuelInventoryId").GetString()!;
            var inventory = stock.Records.Single(r => r.Kind == "inventory" && r.EntityId == drill.Id && r.Id == inventoryId);
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(chosen.Key).GetProperty("insertable").GetInt64();
            state = await ObserveAsync();
            int count = checked((int)Math.Min(reserve, state.Inventory.GetValueOrDefault(chosen.Key)));
            if (count < 1 || capacity < count) throw new InvalidOperationException("The observed actor stock or burner capacity cannot supply the fuel load.");
            var receipt = await WorkAsync("insert", new { entityId = drill.Id, inventory = "fuel", item = chosen.Key, count });
            await journal.AppendAsync("stored-extraction-fuel", new { drillId = drill.Id, fuel = chosen.Key, count, reserve, estimatedWorkJoules = work,
                stock.SnapshotId, stock.CollectedTick, receipt.OperationId }, token);
        }

        async Task BootstrapAsync(string wanted, int count)
        {
            bool previous = Preparing.Value; Preparing.Value = true;
            try { await production.ProduceAsync(wanted, count, token); }
            finally { Preparing.Value = previous; }
        }
        async Task<OperationReceipt> WorkAsync(string kind, object args)
        {
            var receipt = await controller.WorkAsync(kind, args, 600, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"Stored extraction {kind} ended with {receipt.Status}: {receipt.Error?.Code}; inspect partial effects.");
            return receipt;
        }
        void RequireScope(ActorScope scope) { if (scope != catalog.Scope) throw new InvalidDataException("Resource extraction actor scope changed."); }
        async Task<ProductionState> ObserveAsync()
        {
            var state = await production.ObserveAsync(token); RequireScope(state.Scope);
            if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
            return state;
        }
        async Task<SpatialSnapshot> MapAsync() { var value = await spatial.CaptureAsync(equipment, 48, token); RequireScope(value.Scope); return value; }
        Dictionary<string, KnownProductionMachine> Owned(ProductionState state) => state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id))
            .ToDictionary(e => e.Id, e => e.AsMachine(), StringComparer.Ordinal);
        Task<ResourceExtractionPlan?> PlanAsync(ProductionState state) => ControllerPlanning.RunAsync(
            cancellation => planner.Find(item, catalog, map, state.Inventory, Owned(state), token: cancellation), controller, TimeSpan.FromMinutes(5), token);
    }
}
