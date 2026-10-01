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
    /// <summary>Minutes of its planned share a planned cell's input chest holds between actor visits.</summary>
    public const double BufferMinutes = 10;
    /// <summary>Crafts an input chest holds at least, and all a cell outside the plan keeps.</summary>
    public const int MinimumBufferCrafts = 5;
    /// <summary>Minutes of its planned rate a product may stock before its producers stop being refilled.</summary>
    public const double StockMinutes = 20;
    /// <summary>Stock that pauses the producers of an item outside the plan.</summary>
    public const long UnplannedStock = 50;
    /// <summary>Free bag slots below which surplus goes back to its producers' output chests before collecting.</summary>
    public const int MinimumFreeSlots = 10;

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

        var shares = CellShares(catalog, state);
        // Demand pulls production: a producer whose product already holds enough stock is not refilled and drains to a stop.
        var caps = StockCaps(catalog, state);
        var stocks = snapshot.SummarizeStocks().InventoryItems;
        var paused = cells.Where(c => c.Kind is "assembler" or FurnaceCellPlanner.Kind && c.Recipe is not null)
            .Select(c => (Cell: c, Product: catalog.Recipes.Single(r => r.Name == c.Recipe).Products[0].Name))
            .Where(p => Paused(caps, p.Product, stocks.GetValueOrDefault(p.Product))).ToArray();
        if (paused.Length > 0)
            await journal.AppendAsync("factory-cells-paused", paused.Select(p => new { p.Cell.Id, p.Cell.Recipe, p.Product,
                stock = stocks.GetValueOrDefault(p.Product), cap = caps!.GetValueOrDefault(p.Product, UnplannedStock) }), token);

        // The bag carries what chests, labs and burners need plus two stacks; a full bag fails every later take.
        var needs = Refills(snapshot).GroupBy(r => r.Item).ToDictionary(g => g.Key, g => g.Sum(r => Math.Max(0, r.Target - r.Loaded)), StringComparer.Ordinal);
        int labs = cells.Count(c => c.Kind == "lab");
        foreach (string pack in catalog.Items.Keys.Where(IsSciencePack)) needs[pack] = needs.GetValueOrDefault(pack) + labs * StackSize(pack);
        needs[Fuel] = needs.GetValueOrDefault(Fuel) + FuelReserve(snapshot, cells, StackSize(Fuel)) + StackSize(Fuel);
        long Cap(string item) => CollectCap(needs.GetValueOrDefault(item), StackSize(item));
        var bag = Carried(snapshot);
        if (FreeSlots(snapshot) < MinimumFreeSlots)
            foreach (var (item, surplus) in Surplus(bag, Cap, StackSize))
            {
                if (NearestProducerChest(snapshot, cells, catalog, item) is not { } home) continue;
                long moved = await TransferAsync("insert", home, item, surplus);
                bag[item] = bag.GetValueOrDefault(item) - moved;
                if (moved > 0) await journal.AppendAsync("factory-surplus-deposited", new { item, moved, chest = home }, token);
            }
        foreach (string chest in OutputChests(cells))
        {
            foreach (var (item, count) in Items(snapshot, chest).Where(p => p.Value > 0))
            {
                long wanted = Math.Min(count, Cap(item) - bag.GetValueOrDefault(item));
                if (wanted <= 0) continue;
                long moved = await TransferAsync("take", chest, item, wanted);
                bag[item] = bag.GetValueOrDefault(item) + moved;
                if (moved > 0) collected[item] = collected.GetValueOrDefault(item) + moved;
            }
        }

        snapshot = await snapshots.CaptureAsync(cancellationToken: token);
        Require(snapshot.Scope, catalog);
        var carried = Carried(snapshot);
        // Power and burners come first: recipes burning fuel only take what their thresholds leave.
        long fuelReserve = FuelReserve(snapshot, cells, catalog.Items[Fuel].StackSize);
        var refills = Refills(snapshot);
        // Scarce items are shared before any chest is filled; transfers still go chest by chest to keep one visit each.
        var allotted = new Dictionary<(string Chest, string Item), long>();
        foreach (var item in refills.GroupBy(r => r.Item))
        {
            var wanting = item.Where(r => r.Loaded < r.Target).ToArray();
            long available = Math.Max(0, carried.GetValueOrDefault(item.Key) - (item.Key == Fuel ? fuelReserve : 0));
            var split = PlanShares(wanting.Select(r => (r.Loaded, r.Target)).ToArray(), available);
            for (int index = 0; index < wanting.Length; index++) allotted[(wanting[index].Chest, item.Key)] = split[index];
        }
        foreach (var (chest, item, loaded, target) in refills.Where(r => r.Loaded < r.Target))
        {
            long moved = await RefillAsync(chest, item, allotted[(chest, item)], reportShort: false);
            if (target - loaded > moved) shortfall[item] = shortfall.GetValueOrDefault(item) + target - loaded - moved;
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
        var burners = FuelOrder(Burners(snapshot, cells).Where(b => NeedsDirectFuel(fedBoilers.Contains(b.EntityId), b.Loaded, stack)).ToArray(), cells);
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
            shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + FuelShortfall(loaded, give, stack);
            powerStarved |= power;
        }
        // Band furnaces burn from their input chest, whose inserter loads the fuel slot; they come after power and burners.
        // Like feeder chests, the reserve is topped up whenever coal is carried, but only a low supply is worth a trip.
        foreach (var (cellId, reserve) in await FurnaceBandFuel.ReservesAsync(game, catalog, cells, bufferCrafts, fuel, token))
        {
            var cell = cells.Single(c => c.Id == cellId);
            string chest = cell.Entities["input-chest"];
            long inChest = Items(snapshot, chest).GetValueOrDefault(fuel);
            long moved = await RefillAsync(chest, fuel, reserve - inChest, reportShort: false);
            long burning = cell.Entities.TryGetValue("machine", out var furnace) ? Items(snapshot, furnace).GetValueOrDefault(fuel) : 0;
            long furnaceShort = PowerFuelShortfall(Math.Max(0, reserve - inChest - moved), inChest + moved + burning, stack);
            if (furnaceShort > 0) shortfall[fuel] = shortfall.GetValueOrDefault(fuel) + furnaceShort;
        }
        var result = new LogisticsResult(collected, supplied, shortfall, actions, snapshot.CollectedTick, powerStarved, upkeep, degraded);
        await journal.AppendAsync("factory-logistics", result, token);
        return result;

        bool IsSciencePack(string item) => item.EndsWith("-science-pack", StringComparison.Ordinal);

        int StackSize(string item) => catalog.Items.TryGetValue(item, out var native) ? native.StackSize : 100;

        // Input chest targets of the cells refilled this round: planned buffers, paused producers left out.
        (string Chest, string Item, long Loaded, long Target)[] Refills(FactorySnapshot photograph) =>
            cells.Where(c => c.Recipe is not null && c.Entities.ContainsKey("input-chest") && paused.All(p => p.Cell.Id != c.Id)).SelectMany(cell =>
            {
                NativeRecipe recipe = catalog.Recipes.Single(r => r.Name == cell.Recipe);
                string chest = cell.Entities["input-chest"];
                var inChest = Items(photograph, chest);
                // Fluid chain cells are sized by their own chain, not by the assembler plan, and keep the caller's buffer.
                int crafts = cell.Kind is "assembler" or FurnaceCellPlanner.Kind ? BufferCrafts(shares, cell.Recipe!, bufferCrafts) : bufferCrafts;
                return recipe.Ingredients.Where(i => i.DeterministicItem).Select(i => (Chest: chest, Item: i.Name,
                    Loaded: inChest.GetValueOrDefault(i.Name), Target: checked((long)(i.Amount!.Value * crafts))));
            }).ToArray();

        // Tops a chest up from carried stock and returns what moved; what the actor lacks is shortfall unless the caller judges it.
        async Task<long> RefillAsync(string chest, string item, long need, bool reportShort = true)
        {
            if (need <= 0) return 0;
            long moved = 0;
            long give = Math.Min(need, carried.GetValueOrDefault(item));
            if (give > 0)
            {
                moved = await TransferAsync("insert", chest, item, give);
                carried[item] = carried.GetValueOrDefault(item) - moved;
                supplied[item] = supplied.GetValueOrDefault(item) + moved;
            }
            if (reportShort && need > moved) shortfall[item] = shortfall.GetValueOrDefault(item) + need - moved;
            return moved;
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
    /// Burners in fuelling order: drills of the cells that mine the fuel first, because once they burn they feed every
    /// other burner through their chests; then power sources, whose starvation stops electric cells; then the rest.
    /// </summary>
    internal static IReadOnlyList<(string EntityId, long Loaded, bool PowerSource)> FuelOrder(
        IReadOnlyList<(string EntityId, long Loaded, bool PowerSource)> burners, IEnumerable<FactoryCell> cells)
    {
        var producers = cells.Where(c => c.IsResource && c.Recipe == Fuel).Select(c => c.Entities.GetValueOrDefault("drill"))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        return burners.OrderByDescending(b => producers.Contains(b.EntityId)).ThenByDescending(b => b.PowerSource).ToArray();
    }

    /// <summary>
    /// Fuel a starved burner still lacks to reach a quarter stack. It sizes procurement to restart burners, not to fill them:
    /// full stacks made the actor extract 250 coal through its early drill before the coal cell could deliver.
    /// </summary>
    internal static long FuelShortfall(long loaded, long given, long stack) => Math.Max(0, stack / 4 - loaded - given);

    /// <summary>
    /// Crafts per minute each ready cell of a planned recipe must deliver under the registered automation targets, or null
    /// when the registry holds no target (prepared fixtures and older registries keep flat buffers).
    /// </summary>
    internal static IReadOnlyDictionary<string, double>? CellShares(ProductionCatalog catalog, FactoryState state)
    {
        var machines = FactoryDirector.MachineItems(catalog);
        if (state.Targets is not { Count: > 0 } targets || machines.Count == 0) return null;
        return AutomationPlanner.Plan(catalog, targets, machines).Stages.ToDictionary(s => s.Recipe, s => s.CraftsPerMinute
            / Math.Max(1, state.Cells.Count(c => c.Kind == s.Kind && c.Recipe == s.Recipe && c.Status == "ready")), StringComparer.Ordinal);
    }

    /// <summary>What the bag may hold of an item after collection: its need plus two stacks of slack for construction.</summary>
    internal static long CollectCap(long need, int stackSize) => need + 2L * stackSize;

    /// <summary>Carried items beyond their cap, largest surplus in stacks first.</summary>
    internal static IReadOnlyList<(string Item, long Surplus)> Surplus(IReadOnlyDictionary<string, long> carried, Func<string, long> cap,
        Func<string, int> stackSize) => carried.Select(p => (Item: p.Key, Surplus: p.Value - cap(p.Key))).Where(p => p.Surplus > 0)
            .OrderByDescending(p => (double)p.Surplus / stackSize(p.Item)).ThenBy(p => p.Item, StringComparer.Ordinal).ToArray();

    /// <summary>Free slots of the actor's main inventory in the photograph; unknown slots never trigger a deposit.</summary>
    internal static int FreeSlots(FactorySnapshot snapshot)
    {
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        string main = actor.Data.GetProperty("mainInventoryId").GetString()!;
        var inventory = snapshot.Records.Single(r => r.Id == main && r.Kind == "inventory");
        return inventory.Data.TryGetProperty("usableSlots", out var usable) && inventory.Data.TryGetProperty("stacks", out var stacks)
            ? usable.GetInt32() - stacks.GetArrayLength() : int.MaxValue;
    }

    /// <summary>
    /// The output chest of a ready cell producing the item nearest to the actor: surplus returns where logistics would
    /// collect it again, still counted in the known factory stock.
    /// </summary>
    internal static string? NearestProducerChest(FactorySnapshot snapshot, IEnumerable<FactoryCell> cells, ProductionCatalog catalog, string item)
    {
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        var at = actor.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        return cells.Where(c => c.Entities.ContainsKey("output-chest") && c.Recipe is not null
                && (catalog.Recipes.FirstOrDefault(r => r.Name == c.Recipe)?.Products[0].Name ?? c.Recipe) == item)
            .Select(c => c.Entities["output-chest"])
            .Select(id => (Id: id, Record: snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.EntityId == id)))
            .Where(p => p.Record is not null)
            .OrderBy(p => p.Record!.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(at))
            .Select(p => p.Id).FirstOrDefault();
    }

    /// <summary>
    /// Stock each planned item may hold before its producers pause: twenty minutes of its whole-factory planned rate, or null
    /// when the registry holds no target (prepared fixtures and older registries never pause).
    /// </summary>
    internal static IReadOnlyDictionary<string, long>? StockCaps(ProductionCatalog catalog, FactoryState state)
    {
        var machines = FactoryDirector.MachineItems(catalog);
        if (state.Targets is not { Count: > 0 } targets || machines.Count == 0) return null;
        return AutomationPlanner.Plan(catalog, targets, machines).Stages.ToDictionary(s => s.Item, s => (long)Math.Ceiling(s.CraftsPerMinute
            * catalog.Recipes.Single(r => r.Name == s.Recipe).Products[0].Amount!.Value * StockMinutes - 1e-9), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a producer of the item pauses: its known stock reached its cap, or the unplanned cap for an item outside
    /// the plan. On 2026-09-30 (seed 20261002) unregulated cells stocked 1338 red packs and 509 magazines while green
    /// science starved.
    /// </summary>
    internal static bool Paused(IReadOnlyDictionary<string, long>? caps, string product, long stock) =>
        caps is not null && stock >= (caps.TryGetValue(product, out long cap) ? Math.Max(1, cap) : UnplannedStock);

    /// <summary>
    /// Crafts an input chest holds: ten minutes of the cell's planned share within [minimum, maximum], the minimum for a
    /// cell outside the plan, or the caller's maximum when no plan exists. On 2026-09-30 (seed 20261002) flat 40-craft
    /// buffers let a magazine cell nobody planned hold 160 iron plates while the science cells starved.
    /// </summary>
    internal static int BufferCrafts(IReadOnlyDictionary<string, double>? shares, string recipe, int maximum) => shares is null ? maximum
        : shares.TryGetValue(recipe, out double perCell)
            ? (int)Math.Clamp(Math.Ceiling(perCell * BufferMinutes - 1e-9), Math.Min(MinimumBufferCrafts, maximum), maximum)
            : Math.Min(MinimumBufferCrafts, maximum);

    /// <summary>
    /// Splits a carried item among input chests: every chest first reaches a quarter of its target, then chests are filled
    /// in order. On 2026-09-30 (seed 20261002) registry-order refills gave every gear to the first consumers and left the
    /// inserter cell empty for two hours.
    /// </summary>
    internal static long[] PlanShares(IReadOnlyList<(long Loaded, long Target)> chests, long available)
    {
        var plan = new long[chests.Count];
        foreach (bool quarter in new[] { true, false })
            for (int index = 0; index < chests.Count && available > 0; index++)
            {
                long goal = quarter ? chests[index].Target / 4 : chests[index].Target;
                long give = Math.Min(available, Math.Max(0, goal - chests[index].Loaded - plan[index]));
                plan[index] += give;
                available -= give;
            }
        return plan;
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
    /// Coal worth a procurement trip for a feeder or band furnace chest. Chests are topped up to their target whenever coal is
    /// carried, but only a supply below a quarter stack, chest and burner together, is reported short: small refills must not
    /// make the actor a coal miner.
    /// </summary>
    internal static long PowerFuelShortfall(long need, long supply, long stack) => supply < stack / 4 ? need : 0;

    /// <summary>
    /// Fuel recipe chests leave to power and burners: what lifts every feeder supply, chest and boiler together, and every burner
    /// fuelled by hand to a quarter stack. Plastic burns coal too, and a starved boiler would stop it with the whole factory.
    /// </summary>
    internal static long FuelReserve(FactorySnapshot snapshot, IReadOnlyList<FactoryCell> cells, long stack)
    {
        long threshold = stack / 4;
        var feeders = cells.Where(c => c.Kind == "power" && c.Entities.ContainsKey("input-chest")).ToArray();
        long reserve = feeders.Sum(c =>
        {
            var chest = Items(snapshot, c.Entities["input-chest"]);
            long boiler = c.Entities.TryGetValue("boiler", out var id) ? Items(snapshot, id).GetValueOrDefault(Fuel) : 0;
            return PowerFuelNeed(chest, Fuel, stack) == 0 ? 0 : Math.Max(0, threshold - chest.GetValueOrDefault(Fuel) - boiler);
        });
        var fed = cells.Where(c => c.Kind == "power").Select(c => c.Entities.GetValueOrDefault("boiler")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return reserve + Burners(snapshot, cells).Where(b => NeedsDirectFuel(fed.Contains(b.EntityId), b.Loaded, stack))
            .Sum(b => Math.Max(0, threshold - b.Loaded));
    }

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
