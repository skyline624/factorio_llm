using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record AutomatedSmeltingResult(string Item, int TargetStock, long InitialStock, long FinalStock,
    string DrillId, string FurnaceId, long StartTick, long EndTick);

/// <summary>Installs direct native extraction into an existing compatible furnace and observes real production.</summary>
public sealed class AutomatedSmeltingController(IGameClient game, IControllerJournal journal)
{
    public async Task<AutomatedSmeltingResult> RunAsync(string item, int targetStock, CancellationToken token = default)
    {
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var production = new ProductionController(game, journal);
        var spatial = new SpatialClient(game);
        await using var controller = new SpatialController(game, journal);
        ProductionState initial = await production.ObserveAsync(token);
        await journal.AppendAsync("automated-smelting-start", new { item, targetStock, initial }, token);
        if (initial.Inventory.GetValueOrDefault(item) >= targetStock) throw new InvalidOperationException("The requested stock is already available.");
        var assessment = await CaptureAsync(item, initial, token);
        ProductionCatalog catalog = assessment.Catalog;
        SpatialSnapshot map = assessment.Map;
        SmeltingPlan plan = assessment.Plan
            ?? throw new InvalidOperationException("No observed resource patch supports direct extraction into a compatible existing furnace.");
        NativeRecipe recipe = plan.Recipe;
        SpatialEntity drill;
        SpatialEntity receiver = map.Entities.Single(e => e.Id == plan.Connection.ReceiverId);
        if (plan.ExistingDrillId is not null)
        {
            drill = map.Entities.Single(e => e.Id == plan.ExistingDrillId);
            await journal.AppendAsync("extraction-reuse", new { drill.Id, receiverId = receiver.Id, map.CollectedTick }, token);
        }
        else
        {
            var chosen = (Item: plan.DrillItem, Plan: plan.Connection);
            await journal.AppendAsync("extraction-plan", new { map.Scope, map.CollectedTick, chosen.Item, chosen.Plan }, token);
            await controller.TravelAsync(chosen.Plan.Drill.Position, 3, catalog, token);
            map = await spatial.CaptureAsync([chosen.Item], radius: 48, cancellationToken: token);
            RequireScope(map.Scope);
            receiver = map.Entities.Single(e => e.Id == chosen.Plan.ReceiverId);
            var constructionMap = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
            if (!new ExtractionPlanner().Find(new SpatialCollisionField(constructionMap), chosen.Item, recipe.Ingredients[0].Name, catalog, [receiver])
                .Any(p => p.Drill.Position == chosen.Plan.Drill.Position && p.Drill.Direction == chosen.Plan.Drill.Direction))
                throw new InvalidOperationException("Extraction geometry changed before construction; replan from observations.");
            string builtId = await new PoweredMachineController(game, journal).BuildAtAsync(chosen.Item,
                chosen.Plan.Drill, catalog, controller, token, [receiver.Position]);
            map = await spatial.CaptureAsync([plan.DrillItem], radius: 48, cancellationToken: token);
            RequireScope(map.Scope);
            drill = map.Entities.Single(e => e.Id == builtId);
            receiver = map.Entities.Single(e => e.Id == receiver.Id);
            if (drill.DropPosition is null || !ExtractionPlanner.DropTile(drill.DropPosition).Overlaps(receiver.Bounds)
                || (drill.DropTargetId is not null && drill.DropTargetId != receiver.Id)
                || drill.DropPosition.DistanceTo(chosen.Plan.OutputPosition) > 0.01)
                throw new InvalidDataException("Native drill output does not match the computed connection.");
        }
        string drillId = drill.Id;
        await journal.AppendAsync("extraction-connection", new
        {
            drill.Id,
            drill.Position,
            drill.Direction,
            drill.DropPosition,
            drill.DropTargetId,
            receiverId = receiver.Id,
            receiver.Bounds,
            status = "geometry-verified-awaiting-production"
        }, token);
        var fuelPlan = ChooseFuel(initial, includeDrillReserve: true);
        await journal.AppendAsync("smelting-fuel-plan", new { fuelPlan, map.CollectedTick,
            interpretation = "Conservative work estimate and stack-bounded reserve; completion still requires native output." }, token);
        bool electric = map.Prototypes[drill.Name].IsElectric;
        if (electric)
        {
            var supplied = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(supplied.Scope);
            int batches = checked((int)Math.Ceiling((targetStock - initial.Inventory.GetValueOrDefault(item)) / recipe.Products[0].Amount!.Value));
            if (FurnaceRequirements.From(supplied, receiver.Id, recipe, batches).InputToInsert > 0)
                await EnsurePowerAsync(fuelPlan.DrillWorkJoules, reserve: true);
        }
        for (int iteration = 0; iteration < 1800; iteration++)
        {
            ProductionState state = await production.ObserveAsync(token);
            RequireScope(state.Scope);
            if (state.Inventory.GetValueOrDefault(item) >= targetStock)
            {
                var result = new AutomatedSmeltingResult(item, targetStock, initial.Inventory.GetValueOrDefault(item),
                    state.Inventory.GetValueOrDefault(item), drillId, receiver.Id, initial.Tick, state.Tick);
                await journal.AppendAsync("automated-smelting-result", result, token);
                return result;
            }
            ProductionEntity furnace = state.Entities.Single(e => e.Id == receiver.Id);
            if (furnace.Count("output", item) > 0)
            {
                await controller.TravelAsync(receiver.Position, 3, catalog, token);
                await WorkAsync("take", new
                {
                    entityId = receiver.Id,
                    inventory = "output",
                    item,
                    count = Math.Min(furnace.Count("output", item), targetStock - state.Inventory.GetValueOrDefault(item))
                });
                continue;
            }
            FactorySnapshot supplied = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(supplied.Scope);
            int batches = checked((int)Math.Ceiling((targetStock - state.Inventory.GetValueOrDefault(item)) / recipe.Products[0].Amount!.Value));
            FurnaceRequirements remaining = FurnaceRequirements.From(supplied, receiver.Id, recipe, batches);
            await journal.AppendAsync("smelting-supply-requirements", new { supplied.SnapshotId, supplied.CollectedTick,
                receiverId = receiver.Id, batches, remaining }, token);
            if (remaining.ReadyOutput > 0) continue;
            bool needsExtraction = remaining.InputToInsert > 0;
            if (needsExtraction && iteration % 10 == 0
                && await new ExtractionRecoveryController(game, journal).TryRecoverAsync(drillId, catalog, controller, token))
            {
                await new SmeltingPreparationController(game, journal).PrepareAsync(item, token);
                var continuation = await new AutomatedSmeltingController(game, journal).RunAsync(item, targetStock, token);
                var result = continuation with { InitialStock = initial.Inventory.GetValueOrDefault(item), StartTick = initial.Tick };
                await journal.AppendAsync("automated-smelting-result", result, token);
                return result;
            }
            if (needsExtraction && electric && iteration % 10 == 0)
                await EnsurePowerAsync(0, reserve: false);
            else if (needsExtraction && !electric) await FuelAsync(drillId, drill.Position);
            await FuelAsync(receiver.Id, receiver.Position, needsExtraction);
            await WorkAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Automated smelting exhausted its observation budget; inspect the installed machines.");

        async Task EnsurePowerAsync(double energy, bool reserve)
        {
            using var protectedMachines = ProductionReservations.Enter(new HashSet<string>(StringComparer.Ordinal) { drillId, receiver.Id });
            await SmeltingPreparationController.BootstrapAsync(() =>
                new PowerGridController(game, journal).ConnectAsync(drillId, catalog, controller, token));
            await new PoweredMachineController(game, journal).MaintainFuelAsync(drillId, energy, catalog, controller, reserve, token);
            await controller.ApproachEntityAsync(drillId, drill.Position, catalog, token);
            var powered = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            RequireScope(powered.Scope);
            await journal.AppendAsync("smelting-electric-supply", new { drillId, energy, reserve,
                powered.CollectedTick, power = powered.Entities.Single(e => e.Id == drillId).Power }, token);
        }

        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidOperationException("Actor scope changed; reconcile before continuing installation or production.");
        }

        async Task<OperationReceipt> WorkAsync(string kind, object arguments)
        {
            OperationReceipt receipt = await controller.WorkAsync(kind, arguments, 600, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"{kind} ended with {receipt.Status}: {receipt.Error?.Code}. Inspect partial effects before replanning.");
            return receipt;
        }

        SmeltingFuelPlan ChooseFuel(ProductionState state, bool includeDrillReserve)
        {
            var available = new Dictionary<string, long>(state.Inventory, StringComparer.Ordinal);
            foreach (string fuel in catalog.Items.Where(p => p.Value.FuelValue > 0).Select(p => p.Key))
                available[fuel] = checked(available.GetValueOrDefault(fuel) + state.Entities
                    .Where(e => !ProductionReservations.Current.Contains(e.Id)).Sum(e => e.Count("output", fuel)));
            return new SmeltingFuelPlanner().Choose(plan, map, catalog,
                checked((int)Math.Max(1, targetStock - state.Inventory.GetValueOrDefault(item))), state.Inventory, available,
                SmeltingPreparationController.IsPreparing || StoredResourceExtractionController.IsPreparing, includeDrillReserve);
        }

        async Task FuelAsync(string entityId, MapPosition position, bool includeDrillReserve = true)
        {
            ProductionState state = await production.ObserveAsync(token);
            RequireScope(state.Scope);
            if (state.Entities.Single(e => e.Id == entityId).InventoryTotal("fuel") > 0) return;
            var burning = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(burning.Scope);
            if (!NativeBurnerStock.From(burning, entityId).Empty) return;
            fuelPlan = ChooseFuel(state, includeDrillReserve);
            int reserve = entityId == drillId ? fuelPlan.DrillReserve : fuelPlan.FurnaceReserve;
            await journal.AppendAsync("smelting-fuel-reassessment", new { entityId, state.Tick, fuelPlan, includeDrillReserve }, token);
            string fuel = fuelPlan.Fuel;
            int procurement = fuelPlan.ProcurementTarget(entityId == drillId, state.Inventory.GetValueOrDefault(fuel),
                state.Entities.Single(e => e.Id == drillId).Count("fuel", fuel),
                state.Entities.Single(e => e.Id == receiver.Id).Count("fuel", fuel), includeDrillReserve);
            if (procurement > 0)
            {
                var reserved = new HashSet<string>(StringComparer.Ordinal) { drillId, receiver.Id };
                if (fuelPlan.CanProduce) await production.ProduceAsync(fuel, procurement, token, reserved);
                else await production.CollectAvailableAsync(fuel, procurement, token, reserved);
            }
            await controller.TravelAsync(position, 3, catalog, token);
            var stock = await new FactorySnapshotClient(game).CaptureAsync([fuel], cancellationToken: token);
            RequireScope(stock.Scope);
            string inventoryId = stock.Records.Single(r => r.Kind == "entity" && r.EntityId == entityId).Data.GetProperty("fuelInventoryId").GetString()
                ?? throw new InvalidDataException("Missing native fuel inventory identity; update the mod before refuelling.");
            var inventory = stock.Records.Single(r => r.Kind == "inventory" && r.Id == inventoryId && r.EntityId == entityId);
            long loaded = inventory.Data.GetProperty("items").TryGetProperty(fuel, out var amount) ? amount.GetInt64() : 0;
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(fuel).GetProperty("insertable").GetInt64();
            state = await production.ObserveAsync(token);
            RequireScope(state.Scope);
            int count = checked((int)Math.Min(Math.Max(0, reserve - loaded), state.Inventory.GetValueOrDefault(fuel)));
            if (count == 0) return;
            if (capacity < count) throw new InvalidOperationException("Native burner capacity changed; reconcile before transferring the fuel reserve.");
            var transferred = await WorkAsync("insert", new { entityId, inventory = "fuel", item = fuel, count });
            await journal.AppendAsync("smelting-fuel-loaded", new { entityId, fuel, count, reserve, stock.SnapshotId, stock.CollectedTick,
                operationId = transferred.OperationId }, token);
        }
    }

    internal async Task<SmeltingPlan?> AssessAsync(string item, ProductionState state, CancellationToken token) =>
        (await CaptureAsync(item, state, token)).Plan;

    private async Task<(ProductionCatalog Catalog, SpatialSnapshot Map, SmeltingPlan? Plan)> CaptureAsync(
        string item, ProductionState state, CancellationToken token)
    {
        if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (catalog.Scope != state.Scope) throw new InvalidDataException("Production observations span different actor scopes.");
        string[] drillItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "mining-drill")
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        if (drillItems.Length > 16) throw new InvalidOperationException("Mining drill geometry exceeds the snapshot budget.");
        SpatialSnapshot map = await new SpatialClient(game).CaptureAsync(drillItems, radius: 48, cancellationToken: token);
        if (map.Scope != state.Scope) throw new InvalidDataException("Production observations span different actor scopes.");
        SmeltingPlan? plan = new SmeltingPlanner().Find(item, catalog, map, state.Inventory,
            state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)).ToDictionary(e => e.Id, e => e.AsMachine(), StringComparer.Ordinal));
        return (catalog, map, plan);
    }
}
