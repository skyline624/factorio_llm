using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Starts cold resource-cell burners before subsequent construction waits on their output.</summary>
internal sealed class ResourceCellStartup(IGameClient game, IControllerJournal journal)
{
    /// <summary>One native census skips already supplied equipment; each selected cell is reobserved before any transfer.</summary>
    public async Task StartManyAsync(IEnumerable<FactoryCell> cells, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token, Func<CancellationToken, Task<bool>>? isObjectiveComplete = null)
    {
        var producers = cells.Where(c => c.IsResource).ToArray();
        if (producers.Length == 0 || isObjectiveComplete is not null && await isObjectiveComplete(token)) return;
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while inspecting retained resource producers.");
        var cold = producers.Where(c => BurnerEntities(c).Any(id => Cold(snapshot, id))).ToArray();
        await journal.AppendAsync("resource-startup-scan", new { snapshot.Scope, snapshot.CollectedTick,
            inspectedCells = producers.Length, selectedCells = cold.Select(c => c.Id).ToArray() }, token);
        foreach (var cell in cold)
        {
            if (isObjectiveComplete is not null && await isObjectiveComplete(token)) return;
            await StartAsync(cell, catalog, controller, token);
        }
    }

    public async Task StartAsync(FactoryCell cell, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (!cell.IsResource) return;
        var reader = new FactorySnapshotClient(game);
        foreach (string id in BurnerEntities(cell)) await StartBurnerAsync(id);

        async Task StartBurnerAsync(string id)
        {
            var stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
            Require(stock);
            if (!Cold(stock, id)) return; // Electric equipment and already burning equipment need no starter mutation.
            var production = new ProductionController(game, journal);
            var actor = await production.ObserveAsync(token);
            RequireActor(actor);
            if (actor.Inventory.GetValueOrDefault(FactoryLogistics.Fuel) == 0)
                await StoredResourceExtractionController.BootstrapFuelAsync(game, journal, FactoryLogistics.Fuel, token);

            stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
            Require(stock);
            var entity = stock.Records.Single(r => r.Kind == "entity" && r.EntityId == id);
            await controller.ApproachEntityAsync(id, entity.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, catalog, token);
            stock = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
            Require(stock);
            if (!Cold(stock, id)) return;
            actor = await production.ObserveAsync(token);
            RequireActor(actor);
            var inventory = FuelInventory(stock, id);
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(FactoryLogistics.Fuel).GetProperty("insertable").GetInt64();
            int count = StarterCount(actor.Inventory.GetValueOrDefault(FactoryLogistics.Fuel), capacity, catalog.Items[FactoryLogistics.Fuel].StackSize);
            if (count == 0) throw new InvalidOperationException("The cold resource producer has no insertable starter fuel.");
            var receipt = await controller.WorkAsync("insert", new { entityId = id, inventory = "fuel", item = FactoryLogistics.Fuel, count }, 600, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"Resource producer startup ended with {receipt.Status}; reconcile before resuming.");
            var proof = await reader.CaptureAsync([FactoryLogistics.Fuel], cancellationToken: token);
            Require(proof);
            if (Cold(proof, id)) throw new InvalidDataException("The completed starter transfer did not leave native loaded or burning fuel.");
            await journal.AppendAsync(cell.Kind == "miner" && cell.Recipe == FactoryLogistics.Fuel ? "coal-producer-started" : "resource-producer-started",
                new { cell.Id, cell.Recipe, drillId = id, count, receipt.OperationId, proof.CollectedTick }, token);
        }

        void Require(FactorySnapshot snapshot)
        {
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while starting the resource producer.");
        }
        void RequireActor(ProductionState state)
        {
            if (state.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while procuring starter fuel.");
            if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
        }
    }

    internal static int StarterCount(long carried, long capacity, int stack) =>
        checked((int)Math.Max(0, Math.Min(Math.Min(carried, capacity), Math.Max(1, stack / 4))));

    internal static IReadOnlyList<string> BurnerEntities(FactoryCell cell) => !cell.IsResource ? []
        : cell.Kind == "smelter" ? [cell.Entities["drill"], cell.Entities["furnace"]] : [cell.Entities["drill"]];

    internal static bool Cold(FactorySnapshot snapshot, string id)
    {
        var entity = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == id);
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
