using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Starts a cold coal producer before a power reserve can wait on that producer's output.</summary>
internal sealed class CoalProducerStartup(IGameClient game, IControllerJournal journal)
{
    public async Task StartAsync(FactoryCell cell, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (cell.Kind != "miner" || cell.Recipe != FactoryLogistics.Fuel) return;
        var reader = new FactorySnapshotClient(game);
        var stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
        Require(stock);
        if (!Cold(stock, cell)) return; // Electric drills and already burning drills need no starter mutation.
        var production = new ProductionController(game, journal);
        var actor = await production.ObserveAsync(token);
        RequireActor(actor);
        if (actor.Inventory.GetValueOrDefault(FactoryLogistics.Fuel) == 0)
            await StoredResourceExtractionController.BootstrapFuelAsync(game, journal, FactoryLogistics.Fuel, token);

        stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
        Require(stock);
        string id = cell.Entities["drill"];
        var entity = stock.Records.Single(r => r.Kind == "entity" && r.EntityId == id);
        await controller.ApproachEntityAsync(id, entity.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, catalog, token);
        stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
        Require(stock);
        if (!Cold(stock, cell)) return;
        actor = await production.ObserveAsync(token);
        RequireActor(actor);
        var inventory = FuelInventory(stock, id);
        long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(FactoryLogistics.Fuel).GetProperty("insertable").GetInt64();
        int count = StarterCount(actor.Inventory.GetValueOrDefault(FactoryLogistics.Fuel), capacity, catalog.Items[FactoryLogistics.Fuel].StackSize);
        if (count == 0) throw new InvalidOperationException("The cold coal producer has no insertable starter fuel.");
        var receipt = await controller.WorkAsync("insert", new { entityId = id, inventory = "fuel", item = FactoryLogistics.Fuel, count }, 600, token: token);
        if (receipt.Status != "completed") throw new InvalidOperationException($"Coal producer startup ended with {receipt.Status}; reconcile before resuming.");
        var proof = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
        Require(proof);
        if (Cold(proof, cell)) throw new InvalidDataException("The completed starter transfer did not leave native loaded or burning fuel.");
        await journal.AppendAsync("coal-producer-started", new { cell.Id, drillId = id, count, receipt.OperationId, proof.CollectedTick }, token);

        void Require(FactorySnapshot snapshot)
        {
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while starting the coal producer.");
        }
        void RequireActor(ProductionState state)
        {
            if (state.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while procuring starter fuel.");
            if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        }
    }

    internal static int StarterCount(long carried, long capacity, int stack) =>
        checked((int)Math.Max(0, Math.Min(Math.Min(carried, capacity), Math.Max(1, stack / 4))));

    internal static bool Cold(FactorySnapshot snapshot, FactoryCell cell)
    {
        var entity = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == cell.Entities["drill"]);
        if (!entity.Data.TryGetProperty("burnerRemainingJoules", out var burning)) return false;
        if (burning.GetDouble() > 0) return false;
        return !FuelInventory(snapshot, entity.EntityId).Data.GetProperty("items").EnumerateObject().Any(p => p.Value.GetInt64() > 0);
    }

    private static FactoryRecord FuelInventory(FactorySnapshot snapshot, string id)
    {
        string inventoryId = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == id).Data.GetProperty("fuelInventoryId").GetString()!;
        return snapshot.Records.Single(r => r.Kind == "inventory" && r.Id == inventoryId && r.EntityId == id);
    }
}
