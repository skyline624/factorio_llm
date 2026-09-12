using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Replenishes an observed feeder before depletion, without changing its material or endpoints.</summary>
public sealed class FuelReserveController(IGameClient game, IControllerJournal journal)
{
    public async Task<bool> TryMaintainAsync(string boilerId, long networkId, SpatialSnapshot map, ProductionCatalog catalog,
        SpatialController controller, double expectedEnergy, bool reserve, bool bootstrap, CancellationToken token)
    {
        var production = new ProductionController(game, journal);
        var factory = new FactorySnapshotClient(game);
        var spatial = new SpatialClient(game);
        var planner = new FuelReservePlanner();
        ProductionState owned = await ObserveAsync();
        FactorySnapshot stock = await CaptureAsync();
        FuelReservePlan? plan = planner.Choose(map, stock, catalog, boilerId, networkId, owned.Inventory, expectedEnergy, reserve);
        if (plan is null) return false;
        await journal.AppendAsync("fuel-reserve-plan", new { plan, stock.SnapshotId, stock.CollectedTick, expectedEnergy, reserve }, token);
        if (plan.RefillAmount > 0)
        {
            await production.ProduceAsync(plan.Fuel, plan.RefillAmount + (bootstrap ? 2 : 0), token,
                new HashSet<string>(StringComparer.Ordinal) { plan.ChestId });
            var chest = map.Entities.Single(e => e.Id == plan.ChestId);
            await controller.ApproachEntityAsync(chest.Id, chest.Position, catalog, token);
            map = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            RequireScope(map.Scope);
            owned = await ObserveAsync();
            stock = await factory.CaptureAsync([plan.Fuel], cancellationToken: token);
            RequireScope(stock.Scope);
            var current = planner.Choose(map, stock, catalog, boilerId, networkId, owned.Inventory, expectedEnergy, true);
            if (current is null || current.ChestId != plan.ChestId || current.InserterId != plan.InserterId || current.Fuel != plan.Fuel)
                throw new InvalidDataException("Fuel feeder identity or material changed during collection.");
            var inventory = stock.Records.Single(r => r.EntityId == plan.ChestId && r.Kind == "inventory");
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(plan.Fuel).GetProperty("insertable").GetInt64();
            int count = (int)Math.Min(current.RefillAmount, owned.Inventory.GetValueOrDefault(plan.Fuel));
            if (capacity < count) throw new InvalidOperationException("The feeder chest can no longer accept the planned reserve.");
            if (count > 0)
            {
                var receipt = await controller.WorkAsync("insert", new { entityId = plan.ChestId, inventory = "chest", item = plan.Fuel, count }, 600, token: token);
                if (receipt.Status != "completed" || receipt.Effects.GetProperty("transferred").GetInt64() != count)
                    throw new InvalidOperationException("Reserve transfer did not complete as requested; reconcile native effects.");
                FactorySnapshot after = await CaptureAsync();
                var actual = FeederStockReading.From(after, plan.ChestId, plan.InserterId, plan.Fuel);
                await journal.AppendAsync("fuel-reserve-refilled", new { plan.BoilerId, plan.ChestId, plan.InserterId, plan.Fuel,
                    plan.TargetStock, before = current.CurrentStock, transferred = count, after = actual, after.CollectedTick,
                    operationId = receipt.OperationId }, token);
            }
        }
        if (bootstrap)
        {
            owned = await ObserveAsync();
            var boiler = owned.Entities.Single(e => e.Id == boilerId);
            int missing = (int)Math.Max(0, 2 - boiler.Count("fuel", plan.Fuel));
            if (missing > 0)
            {
                await production.ProduceAsync(plan.Fuel, missing, token,
                    new HashSet<string>(StringComparer.Ordinal) { plan.ChestId });
                await controller.ApproachEntityAsync(boilerId, boiler.Position, catalog, token);
                var receipt = await controller.WorkAsync("insert", new { entityId = boilerId, inventory = "fuel", item = plan.Fuel, count = missing }, 600, token: token);
                if (receipt.Status != "completed" || receipt.Effects.GetProperty("transferred").GetInt64() != missing)
                    throw new InvalidOperationException("Feeder bootstrap did not complete as requested; reconcile native effects.");
                await journal.AppendAsync("fuel-reserve-bootstrap", new { boilerId, plan.Fuel, count = missing, receipt.OperationId }, token);
            }
        }
        return true;

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed while renewing the fuel reserve.");
        }
        async Task<ProductionState> ObserveAsync()
        {
            var value = await production.ObserveAsync(token);
            RequireScope(value.Scope);
            if (value.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
            return value;
        }
        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(value.Scope);
            return value;
        }
    }
}
