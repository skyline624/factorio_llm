using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Builds one chest-fed machine cell in a factory band: clears removable obstacles, joins the electric network,
/// places the machine, inserters, chests and pole at C# solved positions and records the native ids.
/// An interrupted cell keeps its slot and reuses the entities already built there.
/// </summary>
public sealed class FactoryCellBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    public const int ZoneSlots = 8;

    public async Task<FactoryCell> BuildAsync(string kind, string machineItem, string? recipe, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var equipment = Equipment(catalog, machineItem);
        bool io = kind != "lab";
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        await using var controller = new SpatialController(game, journal);
        var spatial = new SpatialClient(game);
        string[] items = [machineItem, equipment.Inserter, equipment.Chest, equipment.Pole];

        FactoryCell? cell = state.Cells.FirstOrDefault(c => c.Status == "building" && c.Kind == kind && c.MachineItem == machineItem && c.Recipe == recipe);
        FactoryZone zone;
        if (cell is not null) zone = state.Zones.Single(z => z.Id == cell.Zone);
        else
        {
            var geometryMap = await spatial.CaptureAsync(items, 48, token);
            RequireScope(geometryMap.Scope, catalog);
            EntityGeometry machine = geometryMap.Prototypes[geometryMap.Items[machineItem].EntityName];
            FactoryZone? open = state.Zones.FirstOrDefault(z => z.Pitch == FactoryBandPlanner.Pitch(machine)
                && z.BandHeight == FactoryBandPlanner.BandHeight(machine) && FactoryRegistry.NextSlot(state, z) is not null);
            zone = open ?? await CreateZoneAsync(machine);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var slot = FactoryRegistry.NextSlot(state, zone)!;
            cell = new($"cell-{Guid.NewGuid():N}", zone.Id, slot, kind, machineItem, recipe, new Dictionary<string, string>(), "building", geometryMap.CollectedTick);
            await registry.SaveAsync(state.With(cell), token);
        }

        var planningMap = await spatial.CaptureAsync(items, 48, token);
        RequireScope(planningMap.Scope, catalog);
        CellLayout layout = new FactoryBandPlanner().Layout(planningMap, equipment, zone.Origin, cell.Slot, io, io);
        await journal.AppendAsync("factory-cell-plan", new { cell.Id, kind, recipe, zone, layout }, token);
        await EnsureItemsAsync(layout.Entities.GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));

        var walkway = new MapPosition((layout.Walkway.Min.X + layout.Walkway.Max.X) / 2, (layout.Walkway.Min.Y + layout.Walkway.Max.Y) / 2);
        await controller.TravelAsync(walkway, 1, catalog, token);
        await ClearAsync(layout);

        var ids = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        // The pole goes first so power problems are discovered before machines are committed.
        foreach (var planned in layout.Entities.OrderBy(e => e.Role == "pole" ? 0 : e.Role == "machine" ? 1 : 2))
        {
            if (ids.ContainsKey(planned.Role)) continue;
            ids[planned.Role] = await PlaceAsync(planned);
            cell = cell with { Entities = new Dictionary<string, string>(ids, StringComparer.Ordinal) };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            if (planned.Role == "pole") await ConnectPowerAsync(ids["pole"], planned);
        }
        if (recipe is not null)
        {
            var configured = await controller.WorkAsync("set_recipe", new { entityId = ids["machine"], recipe }, 600, token: token);
            Completed(configured, "set_recipe");
        }
        var built = await spatial.CaptureAsync(items, 32, token);
        RequireScope(built.Scope, catalog);
        var machineEntity = built.Entities.SingleOrDefault(e => e.Id == ids["machine"])
            ?? throw new InvalidDataException("The built machine is not observed at its cell.");
        var poleEntity = built.Entities.Single(e => e.Id == ids["pole"]);
        if (machineEntity.Power?.NetworkId is null || machineEntity.Power.NetworkId != poleEntity.Power?.NetworkId)
            throw new InvalidOperationException("The cell machine is not on the cell pole's electric network.");
        cell = cell with { Status = "ready", Tick = built.CollectedTick };
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        await journal.AppendAsync("factory-cell-ready", cell, token);
        return cell;

        async Task<FactoryZone> CreateZoneAsync(EntityGeometry machine)
        {
            var map = await spatial.CaptureAsync(items, 48, token);
            RequireScope(map.Scope, catalog);
            var powered = map.Entities.Where(e => map.Prototypes[e.Name].Type == "electric-pole" && e.Power?.NetworkId is not null
                    && map.Entities.Any(o => o.Id != e.Id && o.Power?.NetworkId == e.Power.NetworkId && IsPowerSource(map.Prototypes[o.Name].Type)))
                .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).FirstOrDefault()
                ?? throw new InvalidOperationException("A factory zone needs an observed generator network near the actor; build steam power first.");
            var site = new FactoryZonePlanner().Find(map, machine, powered.Position, ZoneSlots, token)
                ?? throw new InvalidOperationException("No dry, deposit-free rectangle for a factory band near the power network.");
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var created = new FactoryZone(current.Zones.Count == 0 ? 1 : current.Zones.Max(z => z.Id) + 1, site.Origin, site.Slots,
                FactoryBandPlanner.Pitch(machine), FactoryBandPlanner.BandHeight(machine));
            await registry.SaveAsync(current with { Zones = [.. current.Zones, created] }, token);
            await journal.AppendAsync("factory-zone", new { created, site.Clearance.Count, sourcePole = powered.Id, map.CollectedTick }, token);
            return created;
        }

        async Task EnsureItemsAsync(IReadOnlyDictionary<string, int> needed)
        {
            var placed = new HashSet<string>(cell!.Entities.Keys, StringComparer.Ordinal);
            foreach (var (item, count) in needed)
            {
                int missing = count - layout.Entities.Count(e => e.Item == item && placed.Contains(e.Role));
                if (missing <= 0) continue;
                await EnsureCarriedAsync(item, missing);
            }
        }

        async Task EnsureCarriedAsync(string item, int count)
        {
            var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory.GetValueOrDefault(item);
            if (carried >= count) return;
            using (ProductionReservations.Enter(await registry.CellEntityIdsAsync(catalog.Scope.WorldId, token)))
                await new ProductionGoalExecutor(game, journal).RunAsync(item, Math.Min(1000, count), token);
        }

        async Task ClearAsync(CellLayout plan)
        {
            var area = new WorldBox(new(Math.Min(plan.Footprint.Min.X, plan.Walkway.Min.X) - .5, Math.Min(plan.Footprint.Min.Y, plan.Walkway.Min.Y) - .5),
                new(Math.Max(plan.Footprint.Max.X, plan.Walkway.Max.X) + .5, Math.Max(plan.Footprint.Max.Y, plan.Walkway.Max.Y) + .5));
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var map = await spatial.CaptureAsync(items, 32, token);
                RequireScope(map.Scope, catalog);
                var obstacle = map.Entities.Where(e => FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type) && e.Bounds.Overlaps(area))
                    .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).FirstOrDefault();
                if (obstacle is null) return;
                await controller.TravelAsync(obstacle.Position, 2, catalog, token);
                var mined = await controller.WorkAsync("mine", new { name = obstacle.Name, position = obstacle.Position, count = 1 }, 1800, token: token);
                if (mined.Status is not ("completed" or "partial")) throw new InvalidOperationException($"Clearing {obstacle.Name} ended with {mined.Status}: {mined.Error?.Code}.");
                await journal.AppendAsync("factory-clearance", new { obstacle.Id, obstacle.Name, obstacle.Position }, token);
            }
            throw new InvalidOperationException("Cell clearance exceeded its obstacle budget.");
        }

        async Task<string> PlaceAsync(PlannedEntity planned)
        {
            var map = await spatial.CaptureAsync(items, 32, token);
            RequireScope(map.Scope, catalog);
            string entityName = map.Items[planned.Item].EntityName;
            var existing = map.Entities.FirstOrDefault(e => e.Name == entityName && e.Position.DistanceTo(planned.Position) < .01
                && (map.Prototypes[entityName].Type is "container" or "electric-pole" || e.Direction == planned.Direction));
            if (existing is not null) return existing.Id; // Built before an interruption; the receipt was already applied.
            return await new PoweredMachineController(game, journal).BuildAtAsync(planned.Item, new(planned.Position, planned.Direction, 0),
                catalog, controller, token);
        }

        async Task ConnectPowerAsync(string poleId, PlannedEntity planned)
        {
            for (int link = 0; link < 24; link++)
            {
                var map = await spatial.CaptureAsync(items, 48, token);
                RequireScope(map.Scope, catalog);
                var pole = map.Entities.Single(e => e.Id == poleId);
                if (pole.Power?.NetworkId is { } network && map.Entities.Any(e => e.Id != poleId && e.Power?.NetworkId == network
                    && (map.Prototypes[e.Name].Type == "electric-pole" || IsPowerSource(map.Prototypes[e.Name].Type)))) return;
                var owned = map.Entities.Where(e => e.Force == pole.Force && e.Id != poleId).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                var next = new PowerGridPlanner().Next(ReserveZone(map, zone, equipment.Pole), equipment.Pole, pole.Bounds, owned, token);
                await journal.AppendAsync("factory-power-link", new { poleId, next, map.CollectedTick }, token);
                if (next.Status == PowerGridSearchStatus.Connected) return;
                if (next.Status != PowerGridSearchStatus.Extension || next.Pole is null)
                    throw new InvalidOperationException($"The cell pole cannot join the observed network: {next.Status}.");
                // The cell's own pole is already placed: a link needs one more carried pole.
                await EnsureCarriedAsync(equipment.Pole, 1);
                await new PoweredMachineController(game, journal).BuildAtAsync(equipment.Pole, next.Pole, catalog, controller, token);
                await controller.TravelAsync(planned.Position, 6, catalog, token);
            }
            throw new InvalidOperationException("Joining the cell to the electric network exceeded its link budget.");
        }
    }

    /// <summary>Marks the whole band as occupied so power links never take a future cell's slot.</summary>
    public static SpatialSnapshot ReserveZone(SpatialSnapshot map, FactoryZone zone, string poleItem)
    {
        const string name = "factory-zone-reservation";
        var box = new WorldBox(zone.Origin, new(zone.Origin.X + zone.Slots * zone.Pitch, zone.Origin.Y + zone.BandHeight));
        var pole = map.Prototypes[map.Items[poleItem].EntityName];
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [name] = pole with { Name = name, Type = "reservation" } },
            Entities = [.. map.Entities, new SpatialEntity(name, name,
                new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2), box, 0, "planned")]
        };
    }

    /// <summary>Best currently craftable cell equipment; machines keep the caller's choice.</summary>
    public static CellEquipment Equipment(ProductionCatalog catalog, string machineItem)
    {
        bool Enabled(string item) => catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));
        string chest = Enabled("iron-chest") ? "iron-chest" : "wooden-chest";
        if (!Enabled("inserter") || !Enabled("small-electric-pole"))
            throw new InvalidOperationException("Factory cells need electric inserters and small electric poles; research electronics first.");
        return new(machineItem, "inserter", chest, "small-electric-pole");
    }

    public static bool IsPowerSource(string type) => type is "generator" or "electric-energy-interface" or "solar-panel" or "burner-generator";

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building a factory cell; reconcile partial construction.");
    }

    private static void Completed(OperationReceipt receipt, string action)
    {
        if (receipt.Status != "completed") throw new InvalidOperationException($"Factory {action} ended with {receipt.Status}: {receipt.Error?.Code}.");
    }
}
