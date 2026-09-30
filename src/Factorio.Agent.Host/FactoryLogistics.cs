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

        foreach (string chest in OutputChests(cells))
        {
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
        // Steam supply, cell furnaces and burner drills stop without fuel. Keep each burner above a quarter stack of coal.
        const string fuel = "coal";
        long stack = catalog.Items[fuel].StackSize;
        var burners = Burners(snapshot, cells);
        var plan = PlanFuel(burners.Select(b => b.Loaded).ToArray(), carried.GetValueOrDefault(fuel), stack);
        for (int index = 0; index < burners.Count; index++)
        {
            var (burner, loaded) = burners[index];
            if (loaded >= stack / 4) continue;
            long give = 0;
            if (plan[index] > 0)
            {
                give = await TransferAsync("insert", burner, fuel, plan[index], "fuel");
                carried[fuel] = carried.GetValueOrDefault(fuel) - give;
                supplied[fuel] = supplied.GetValueOrDefault(fuel) + give;
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

    /// <summary>Output chests of ready cells: assembler products, smelted plates and mined resources alike.</summary>
    internal static IReadOnlyList<string> OutputChests(IEnumerable<FactoryCell> cells) => cells
        .Where(c => c.Status == "ready" && c.Entities.ContainsKey("output-chest")).Select(c => c.Entities["output-chest"]).ToArray();

    /// <summary>Boilers and every fuelled entity of a cell (furnaces, burner drills) with the fuel they hold, by native id.</summary>
    internal static IReadOnlyList<(string EntityId, long Loaded)> Burners(FactorySnapshot snapshot, IEnumerable<FactoryCell> cells)
    {
        var cellIds = cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        return snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
                && r.Data.TryGetProperty("fuelInventoryId", out _)
                && (r.Data.GetProperty("type").GetString() == "boiler" || cellIds.Contains(r.EntityId)))
            .Select(r =>
            {
                string inventoryId = r.Data.GetProperty("fuelInventoryId").GetString()!;
                var inventory = snapshot.Records.SingleOrDefault(i => i.Id == inventoryId && i.Kind == "inventory");
                return (r.EntityId, inventory is null ? 0 : inventory.Data.GetProperty("items").EnumerateObject().Sum(p => p.Value.GetInt64()));
            })
            .OrderBy(b => b.EntityId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Splits carried fuel among burners below a quarter stack: every starved burner first reaches a quarter stack,
    /// so scarce coal from a young miner starts all furnaces instead of filling one.
    /// </summary>
    internal static long[] PlanFuel(IReadOnlyList<long> loaded, long available, long stack)
    {
        var plan = new long[loaded.Count];
        long threshold = stack / 4;
        foreach (long target in new[] { threshold, stack })
            for (int index = 0; index < loaded.Count && available > 0; index++)
            {
                if (loaded[index] >= threshold) continue;
                long give = Math.Min(available, Math.Max(0, target - loaded[index] - plan[index]));
                plan[index] += give;
                available -= give;
            }
        return plan;
    }

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
