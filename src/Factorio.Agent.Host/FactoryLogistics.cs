using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>PowerStarved: a boiler stayed below a quarter stack of fuel after this round's distribution.</summary>
public sealed record LogisticsResult(IReadOnlyDictionary<string, long> Collected, IReadOnlyDictionary<string, long> Supplied,
    IReadOnlyDictionary<string, long> Shortfall, int Actions, long Tick, bool PowerStarved = false, MaintenanceResult? Maintenance = null,
    IReadOnlyList<DegradedCell>? Degraded = null);

/// <summary>
/// The actor as the factory's transport: empties cell output chests, then refills input chests and laboratories
/// from what it carries. Every quantity comes from one native factory photograph and the transfer receipts.
/// </summary>
public sealed class FactoryLogistics(IGameClient game, IControllerJournal journal, string directory)
{
    /// <summary>Fuel loaded into boilers, cell furnaces and burner drills.</summary>
    public const string Fuel = "coal";

    public async Task<LogisticsResult> ServiceAsync(int bufferCrafts = 40, CancellationToken token = default)
    {
        if (bufferCrafts is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(bufferCrafts));
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        await using var controller = new SpatialController(game, journal);
        // Upkeep first: destroyed registered entities are rebuilt and turrets rearmed before production transport.
        var upkeep = await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token);
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var cells = state.Cells.Where(c => c.Status == "ready").ToArray();
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        var supplied = new Dictionary<string, long>(upkeep.Supplied, StringComparer.Ordinal);
        var shortfall = new Dictionary<string, long>(upkeep.Shortfall, StringComparer.Ordinal);
        int actions = upkeep.Actions;
        var snapshots = new FactorySnapshotClient(game);
        FactorySnapshot snapshot = await snapshots.CaptureAsync(cancellationToken: token);
        Require(snapshot.Scope, catalog);
        // Destroyed parts and exhausted deposits take a resource cell out of service before it is visited or counted.
        var inspected = ResourceCellHealth.Inspect(snapshot, cells);
        if (inspected.Count > 0)
        {
            await new FactoryRegistry(directory).SaveAsync(inspected.Aggregate(state, (current, cell) => current.With(cell)), token);
            await journal.AppendAsync("resource-cell-health", inspected, token);
            cells = cells.Where(c => inspected.All(i => i.Id != c.Id)).ToArray();
        }
        // A cell whose registered entity is gone, e.g. destroyed by enemies, is journaled once and never serviced blindly.
        var present = new List<FactoryCell>();
        foreach (var cell in cells)
        {
            var missing = Missing(snapshot, cell);
            if (missing.Length == 0) present.Add(cell);
            else await journal.AppendAsync("factory-cell-missing", new { cell.Id, cell.Kind, missing, snapshot.CollectedTick }, token);
        }
        cells = present.ToArray();
        var degraded = FactoryMaintenance.Degraded(cells, FactoryMaintenance.Present(snapshot));
        foreach (var cell in degraded)
            await journal.AppendAsync("factory-cell-degraded", new { cell.Cell, cell.Missing, rebuildable = cell.Missing.All(m => m.Item is not null) }, token);

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
                await RefillAsync(chest, ingredient.Name,
                    checked((long)(ingredient.Amount!.Value * bufferCrafts)) - inChest.GetValueOrDefault(ingredient.Name));
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
        const string fuel = Fuel;
        long stack = catalog.Items[fuel].StackSize;
        // Power cells feed their boilers from a chest; keeping one stack there lets boilers run between actor visits.
        foreach (var cell in cells.Where(c => c.Kind == "power" && c.Entities.ContainsKey("input-chest")))
        {
            string chest = cell.Entities["input-chest"];
            var inChest = Items(snapshot, chest);
            long moved = 0;
            long need = PowerFuelNeed(inChest, fuel, stack);
            if (need == 0 && inChest.Keys.Any(k => k != fuel))
                await journal.AppendAsync("factory-power-chest-mixed", new { cell.Id, chest, inChest }, token);
            long give = Math.Min(need, carried.GetValueOrDefault(fuel));
            if (give > 0)
            {
                moved = await TransferAsync("insert", chest, fuel, give);
                carried[fuel] = carried.GetValueOrDefault(fuel) - moved;
                supplied[fuel] = supplied.GetValueOrDefault(fuel) + moved;
                need -= moved;
            }
            long burning = cell.Entities.TryGetValue("boiler", out var boiler) ? Items(snapshot, boiler).GetValueOrDefault(fuel) : 0;
            long powerShort = PowerFuelShortfall(need, inChest.GetValueOrDefault(fuel) + moved + burning, stack);
            if (powerShort > 0) shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + powerShort;
        }
        // Steam supply, cell furnaces and burner drills stop without fuel. Keep each burner above a quarter stack of coal;
        // a boiler with a feeder cell burns from its chest and is only restarted by hand once completely dry.
        var fedBoilers = cells.Where(c => c.Kind == "power").Select(c => c.Entities.GetValueOrDefault("boiler")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var burners = Burners(snapshot, cells).Where(b => NeedsDirectFuel(fedBoilers.Contains(b.EntityId), b.Loaded, stack)).ToList();
        var plan = PlanFuel(burners.Select(b => b.Loaded).ToArray(), carried.GetValueOrDefault(fuel), stack);
        bool powerStarved = false;
        for (int index = 0; index < burners.Count; index++)
        {
            var (burner, loaded, power) = burners[index];
            long give = 0;
            if (plan[index] > 0)
            {
                give = await TransferAsync("insert", burner, fuel, plan[index], "fuel");
                carried[fuel] = carried.GetValueOrDefault(fuel) - give;
                supplied[fuel] = supplied.GetValueOrDefault(fuel) + give;
            }
            if (loaded + give >= stack / 4) continue;
            shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + stack - loaded - give;
            powerStarved |= power;
        }
        // Band furnaces burn from their input chest, whose inserter loads the fuel slot; they come after power and burners.
        foreach (var (cellId, reserve) in await FurnaceBandFuel.ReservesAsync(game, catalog, cells, bufferCrafts, fuel, token))
        {
            string chest = cells.Single(c => c.Id == cellId).Entities["input-chest"];
            await RefillAsync(chest, fuel, reserve - Items(snapshot, chest).GetValueOrDefault(fuel));
        }
        var result = new LogisticsResult(collected, supplied, shortfall, actions, snapshot.CollectedTick, powerStarved, upkeep, degraded);
        await journal.AppendAsync("factory-logistics", result, token);
        return result;

        bool IsSciencePack(string item) => item.EndsWith("-science-pack", StringComparison.Ordinal);

        // Tops a chest up from carried stock; what the actor lacks is reported as shortfall.
        async Task RefillAsync(string chest, string item, long need)
        {
            if (need <= 0) return;
            long give = Math.Min(need, carried.GetValueOrDefault(item));
            if (give > 0)
            {
                long moved = await TransferAsync("insert", chest, item, give);
                carried[item] = carried.GetValueOrDefault(item) - moved;
                supplied[item] = supplied.GetValueOrDefault(item) + moved;
                need -= moved;
            }
            if (need > 0) shortfall[item] = shortfall.GetValueOrDefault(item) + need;
        }

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

    /// <summary>
    /// Boilers and every fuelled entity of a cell (furnaces, burner drills) with the fuel they hold, by native id.
    /// Boilers are power sources: their starvation stops every electric cell. Band furnaces are fuelled through their
    /// input chest, never by hand.
    /// </summary>
    internal static IReadOnlyList<(string EntityId, long Loaded, bool PowerSource)> Burners(FactorySnapshot snapshot, IEnumerable<FactoryCell> cells)
    {
        var cellIds = cells.Where(c => c.Kind != FurnaceCellPlanner.Kind).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        return snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
                && r.Data.TryGetProperty("fuelInventoryId", out _)
                && (r.Data.GetProperty("type").GetString() == "boiler" || cellIds.Contains(r.EntityId)))
            .Select(r =>
            {
                string inventoryId = r.Data.GetProperty("fuelInventoryId").GetString()!;
                var inventory = snapshot.Records.SingleOrDefault(i => i.Id == inventoryId && i.Kind == "inventory");
                return (r.EntityId, inventory is null ? 0 : inventory.Data.GetProperty("items").EnumerateObject().Sum(p => p.Value.GetInt64()),
                    r.Data.GetProperty("type").GetString() == "boiler");
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

    /// <summary>Fuel a feeder chest still needs to reach the target; a chest holding anything else is never topped up.</summary>
    internal static long PowerFuelNeed(IReadOnlyDictionary<string, long> chest, string fuel, long target) =>
        chest.Any(p => p.Key != fuel && p.Value > 0) ? 0 : Math.Max(0, target - chest.GetValueOrDefault(fuel));

    /// <summary>
    /// Coal worth a procurement trip for a feeder. Chests are topped up to a stack whenever coal is carried, but only a supply
    /// below a quarter stack, chest and boiler together, is reported short: small refills must not make the actor a coal miner.
    /// </summary>
    internal static long PowerFuelShortfall(long need, long supply, long stack) => supply < stack / 4 ? need : 0;

    /// <summary>Registered entities of a cell that the native photograph no longer shows.</summary>
    internal static string[] Missing(FactorySnapshot snapshot, FactoryCell cell) =>
        cell.Entities.Values.Where(id => !snapshot.Records.Any(r => r.Kind == "entity" && r.EntityId == id)).ToArray();

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
