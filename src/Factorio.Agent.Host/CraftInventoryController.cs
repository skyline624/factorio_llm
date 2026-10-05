using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Makes room for native crafting or stock collection by returning surplus to known producer chests.</summary>
internal sealed class CraftInventoryController(IGameClient game, IControllerJournal journal)
{
    internal Task<bool> PrepareAsync(NativeRecipe recipe, int batches, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (ProductionReservations.Factory is null) return Task.FromResult(false);
        var retained = Retained(recipe, batches, CarriedStock.RetainedStock);
        var incoming = recipe.Products.GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => checked((long)g.Sum(p => p.Amount!.Value)), StringComparer.Ordinal);
        return PrepareRoomAsync(incoming, retained, recipe.Name, null, catalog, controller, token);
    }

    internal Task<bool> PrepareCollectionAsync(string item, long quantity, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token) =>
        PrepareIncomingAsync(item, quantity, catalog, controller, token, mining: false);

    internal Task<bool> PrepareMiningAsync(string item, long quantity, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token) =>
        PrepareIncomingAsync(item, quantity, catalog, controller, token, mining: true);

    private Task<bool> PrepareIncomingAsync(string item, long quantity, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token, bool mining)
    {
        if (ProductionReservations.Factory is null) return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(item) || !catalog.Items.ContainsKey(item) || quantity is < 1 or > 1000)
            throw new InvalidDataException("Collection capacity requires a known item and a bounded quantity.");
        // A native tree or rock may yield more than the requested bootstrap item.
        long incoming = mining ? Math.Max(quantity, catalog.Items[item].StackSize) : quantity;
        return PrepareRoomAsync(new Dictionary<string, long>(StringComparer.Ordinal) { [item] = incoming },
            new Dictionary<string, int>(CarriedStock.RetainedStock, StringComparer.Ordinal), null, item,
            catalog, controller, token, mining);
    }

    private async Task<bool> PrepareRoomAsync(IReadOnlyDictionary<string, long> incoming, Dictionary<string, int> retained,
        string? recipe, string? collectionItem, ProductionCatalog catalog, SpatialController controller, CancellationToken token,
        bool mining = false)
    {
        var factory = ProductionReservations.Factory;
        if (factory is null) return false; // Standalone production has no registered storage to deposit into.
        if (factory.WorldId != catalog.Scope.WorldId) throw new InvalidDataException("Inventory storage belongs to another world.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        token = deadline.Token;
        string purpose = mining ? "mining" : collectionItem is null ? "craft" : "stock-collection";
        double storageDistance = mining ? ResourceEquipmentReuse.MaximumStorageDistance : ResourceEquipmentReuse.MaximumDistance;
        var reader = new FactorySnapshotClient(game);
        long latestTick = 0;
        var snapshot = await CaptureAsync(incoming.Keys.ToArray());
        // Never deposit output already accumulated toward the same stock goal.
        foreach (string item in incoming.Keys)
            retained[item] = checked((int)Math.Max(retained.GetValueOrDefault(item), FactoryLogistics.Carried(snapshot).GetValueOrDefault(item)));
        bool changed = false;
        var blocked = new HashSet<(string Entity, string Item)>();
        for (int deposit = 0; !FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming); deposit++)
        {
            if (deposit >= ResourceEquipmentReuse.MaximumDeposits) throw new InvalidOperationException("Incoming stock still lacks native inventory room after bounded surplus deposits.");
            ResourceRecoveryDeposit? home = null;
            foreach (var (item, _) in FactoryLogistics.Surplus(FactoryLogistics.Carried(snapshot),
                item => catalog.Items.TryGetValue(item, out var native)
                    ? Math.Max(retained.GetValueOrDefault(item), FactoryLogistics.CollectCap(0, native.StackSize)) : long.MaxValue,
                item => catalog.Items.TryGetValue(item, out var native) ? native.StackSize : 1))
            {
                snapshot = await CaptureAsync(incoming.Keys.Append(item).Distinct(StringComparer.Ordinal).ToArray());
                home = ResourceEquipmentReuse.DepositOptions(factory, snapshot, catalog, retained, storageDistance)
                    .FirstOrDefault(p => p.Item == item && !blocked.Contains((p.EntityId, p.Item)));
                if (home is not null) break;
            }
            if (home is null)
            {
                await journal.AppendAsync($"{purpose}-inventory-room-unavailable", new { recipe, collectionItem, snapshot.Scope,
                    snapshot.CollectedTick, incoming, retained }, token);
                throw new InvalidOperationException("Incoming stock lacks native room and no observed producer chest accepts the surplus.");
            }
            await controller.ApproachEntityAsync(home.EntityId, home.Position, catalog, token);
            changed = true;
            snapshot = await CaptureAsync(incoming.Keys.Append(home.Item).Distinct(StringComparer.Ordinal).ToArray());
            if (FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, incoming)) return changed;
            var refreshed = ResourceEquipmentReuse.DepositOptions(factory, snapshot, catalog, retained, storageDistance)
                .FirstOrDefault(p => p.EntityId == home.EntityId && p.Item == home.Item);
            if (refreshed is null) { blocked.Add((home.EntityId, home.Item)); continue; }
            var receipt = await controller.WorkAsync("insert", new { entityId = refreshed.EntityId, inventory = "chest",
                item = refreshed.Item, count = refreshed.Count }, 600, token: token);
            long moved = ResourceEquipmentReuse.Transfer(receipt, refreshed.EntityId, refreshed.Item, refreshed.Count, "from_actor");
            await journal.AppendAsync($"{purpose}-inventory-room-deposit", new { recipe, collectionItem, home = refreshed, moved, receipt }, token);
            if (moved == 0) blocked.Add((refreshed.EntityId, refreshed.Item));
            snapshot = await CaptureAsync(incoming.Keys.ToArray());
        }
        return changed;

        async Task<FactorySnapshot> CaptureAsync(IReadOnlyList<string> items)
        {
            var current = await reader.CaptureAsync(items, cancellationToken: token);
            if (current.Scope != catalog.Scope || current.CollectedTick < latestTick)
                throw new InvalidDataException("Inventory capacity observation changed actor scope or regressed in time.");
            latestTick = current.CollectedTick;
            return current;
        }
    }

    internal static Dictionary<string, int> Retained(NativeRecipe recipe, int batches, IReadOnlyDictionary<string, int> kit)
    {
        if (batches is < 1 or > 1000 || recipe.Products.Count is < 1 or > 7
            || recipe.Products.Any(p => !p.DeterministicItem) || recipe.Ingredients.Any(p => !p.DeterministicItem))
            throw new InvalidDataException("Craft capacity requires bounded deterministic solid products and ingredients.");
        var result = new Dictionary<string, int>(kit, StringComparer.Ordinal);
        foreach (var ingredient in recipe.Ingredients.GroupBy(p => p.Name, StringComparer.Ordinal))
            result[ingredient.Key] = Math.Max(result.GetValueOrDefault(ingredient.Key), checked((int)(batches * ingredient.Sum(p => p.Amount!.Value))));
        return result;
    }
}
