using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record LogisticsResult(IReadOnlyDictionary<string, long> Collected, IReadOnlyDictionary<string, long> Supplied,
    IReadOnlyDictionary<string, long> Shortfall, int Actions, long Tick);

/// <summary>
/// The actor as the factory's transport: empties cell output chests, then refills input chests and laboratories
/// from what it carries. Every quantity comes from one native factory photograph and the transfer receipts.
/// </summary>
public sealed class FactoryLogistics(IGameClient game, IControllerJournal journal, string directory)
{
    public async Task<LogisticsResult> ServiceAsync(int bufferCrafts = 40, CancellationToken token = default)
    {
        if (bufferCrafts is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(bufferCrafts));
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var cells = state.Cells.Where(c => c.Status == "ready").ToArray();
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        var supplied = new Dictionary<string, long>(StringComparer.Ordinal);
        var shortfall = new Dictionary<string, long>(StringComparer.Ordinal);
        int actions = 0;
        await using var controller = new SpatialController(game, journal);
        var snapshots = new FactorySnapshotClient(game);
        FactorySnapshot snapshot = await snapshots.CaptureAsync(cancellationToken: token);
        Require(snapshot.Scope, catalog);

        foreach (var cell in cells.Where(c => c.Entities.ContainsKey("output-chest")))
        {
            string chest = cell.Entities["output-chest"];
            foreach (var (item, count) in Items(snapshot, chest).Where(p => p.Value > 0))
            {
                long moved = await TransferAsync("take", chest, item, count);
                if (moved > 0) collected[item] = collected.GetValueOrDefault(item) + moved;
            }
        }

        snapshot = await snapshots.CaptureAsync(cancellationToken: token);
        Require(snapshot.Scope, catalog);
        var carried = Carried(snapshot);
        foreach (var cell in cells.Where(c => c.Recipe is not null && c.Entities.ContainsKey("input-chest")))
        {
            NativeRecipe recipe = catalog.Recipes.Single(r => r.Name == cell.Recipe);
            string chest = cell.Entities["input-chest"];
            var inChest = Items(snapshot, chest);
            foreach (var ingredient in recipe.Ingredients.Where(i => i.DeterministicItem))
            {
                long target = checked((long)(ingredient.Amount!.Value * bufferCrafts));
                long need = target - inChest.GetValueOrDefault(ingredient.Name);
                if (need <= 0) continue;
                long give = Math.Min(need, carried.GetValueOrDefault(ingredient.Name));
                if (give > 0)
                {
                    long moved = await TransferAsync("insert", chest, ingredient.Name, give);
                    carried[ingredient.Name] = carried.GetValueOrDefault(ingredient.Name) - moved;
                    supplied[ingredient.Name] = supplied.GetValueOrDefault(ingredient.Name) + moved;
                    need -= moved;
                }
                if (need > 0) shortfall[ingredient.Name] = shortfall.GetValueOrDefault(ingredient.Name) + need;
            }
        }
        foreach (var cell in cells.Where(c => c.Kind == "lab"))
        {
            string lab = cell.Entities["machine"];
            var loaded = Items(snapshot, lab);
            foreach (var pack in carried.Keys.Where(IsSciencePack).ToArray())
            {
                long give = Math.Min(carried[pack], Math.Max(0, catalog.Items[pack].StackSize - loaded.GetValueOrDefault(pack)));
                if (give <= 0) continue;
                long moved = await TransferAsync("insert", lab, pack, give, "lab");
                carried[pack] -= moved;
                supplied[pack] = supplied.GetValueOrDefault(pack) + moved;
            }
        }
        // Power cells feed their boilers from a chest; keeping one stack there lets boilers run between actor visits.
        const string fuel = "coal";
        foreach (var cell in cells.Where(c => c.Kind == "power" && c.Entities.ContainsKey("input-chest")))
        {
            string chest = cell.Entities["input-chest"];
            var inChest = Items(snapshot, chest);
            long need = PowerFuelNeed(inChest, fuel, catalog.Items[fuel].StackSize);
            if (need == 0 && inChest.Keys.Any(k => k != fuel))
                await journal.AppendAsync("factory-power-chest-mixed", new { cell.Id, chest, inChest }, token);
            long give = Math.Min(need, carried.GetValueOrDefault(fuel));
            if (give > 0)
            {
                long moved = await TransferAsync("insert", chest, fuel, give);
                carried[fuel] = carried.GetValueOrDefault(fuel) - moved;
                supplied[fuel] = supplied.GetValueOrDefault(fuel) + moved;
                need -= moved;
            }
            if (need > 0) shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + need;
        }
        // Steam supply and cell furnaces stop without fuel. Keep each burner above a quarter stack of coal.
        var cellIds = cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var fedBoilers = cells.Where(c => c.Kind == "power").Select(c => c.Entities.GetValueOrDefault("boiler")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var burner in snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && r.Data.TryGetProperty("fuelInventoryId", out _)
            && (r.Data.GetProperty("type").GetString() == "boiler" || cellIds.Contains(r.EntityId))))
        {
            string inventoryId = burner.Data.GetProperty("fuelInventoryId").GetString()!;
            var inventory = snapshot.Records.SingleOrDefault(r => r.Id == inventoryId && r.Kind == "inventory");
            long loaded = inventory is null ? 0 : inventory.Data.GetProperty("items").EnumerateObject().Sum(p => p.Value.GetInt64());
            long stack = catalog.Items[fuel].StackSize;
            if (!NeedsDirectFuel(fedBoilers.Contains(burner.EntityId), loaded, stack)) continue;
            long give = Math.Min(stack - loaded, carried.GetValueOrDefault(fuel));
            if (give > 0)
            {
                long moved = await TransferAsync("insert", burner.EntityId, fuel, give, "fuel");
                carried[fuel] = carried.GetValueOrDefault(fuel) - moved;
                supplied[fuel] = supplied.GetValueOrDefault(fuel) + moved;
                give = moved;
            }
            if (loaded + give < stack / 4) shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + stack - loaded - give;
        }
        var result = new LogisticsResult(collected, supplied, shortfall, actions, snapshot.CollectedTick);
        await journal.AppendAsync("factory-logistics", result, token);
        return result;

        bool IsSciencePack(string item) => item.EndsWith("-science-pack", StringComparison.Ordinal);

        async Task<long> TransferAsync(string kind, string entityId, string item, long count, string inventory = "chest")
        {
            var entity = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == entityId);
            var position = entity.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            await controller.ApproachEntityAsync(entityId, position, catalog, token);
            var receipt = await controller.WorkAsync(kind, new { entityId, inventory, item, count = checked((int)Math.Min(count, 100000)) }, 600, token: token);
            actions++;
            if (receipt.Status is not ("completed" or "partial"))
            {
                // A full bag or an emptied chest is an observed limit, not a reason to retry the transfer.
                await journal.AppendAsync("factory-transfer-refused", new { kind, entityId, item, count, receipt.Status, receipt.Error }, token);
                return 0;
            }
            return receipt.Effects.GetProperty("transferred").GetInt64();
        }
    }

    /// <summary>Fuel a feeder chest still needs to reach the target; a chest holding anything else is never topped up.</summary>
    internal static long PowerFuelNeed(IReadOnlyDictionary<string, long> chest, string fuel, long target) =>
        chest.Any(p => p.Key != fuel && p.Value > 0) ? 0 : Math.Max(0, target - chest.GetValueOrDefault(fuel));

    /// <summary>A boiler with a feeder cell burns from its chest; the actor only restarts it after it ran completely dry.</summary>
    internal static bool NeedsDirectFuel(bool fedByCell, long loaded, long stack) => fedByCell ? loaded == 0 : loaded < stack / 4;

    internal static Dictionary<string, long> Items(FactorySnapshot snapshot, string entityId)
    {
        var items = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var record in snapshot.Records.Where(r => r.Kind == "inventory" && r.EntityId == entityId))
            foreach (var item in record.Data.GetProperty("items").EnumerateObject())
                items[item.Name] = items.GetValueOrDefault(item.Name) + item.Value.GetInt64();
        return items;
    }

    internal static Dictionary<string, long> Carried(FactorySnapshot snapshot)
    {
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        string main = actor.Data.GetProperty("mainInventoryId").GetString()!;
        var inventory = snapshot.Records.Single(r => r.Id == main && r.Kind == "inventory");
        return inventory.Data.GetProperty("items").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
    }

    private static void Require(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during factory logistics; reconcile transfers.");
    }
}
