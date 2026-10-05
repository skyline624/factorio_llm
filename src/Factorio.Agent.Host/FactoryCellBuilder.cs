using System.Text.Json;
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
        if (kind == FurnaceCellPlanner.Kind && FurnaceCellPlanner.Failure(catalog, machineItem, recipe, FactoryLogistics.Fuel) is { } refused)
            throw new InvalidOperationException(refused);
        var equipment = Equipment(catalog, machineItem);
        var (input, output) = Sides(kind);
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        await using var controller = new SpatialController(game, journal);
        var spatial = new SpatialClient(game);
        // Steam power grows beside its installation; bands and their power links keep that room free.
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        string[] items = [machineItem, equipment.Inserter, equipment.Chest, equipment.Pole, .. steam?.Machines ?? []];

        FactoryCell? cell = state.Cells.FirstOrDefault(c => c.Status == "building" && c.Kind == kind && c.MachineItem == machineItem && c.Recipe == recipe);
        FactoryZone zone;
        if (cell is not null) zone = state.Zones.Single(z => z.Id == cell.Zone);
        else
        {
            if (Refusal(state, kind) is { } full) throw new InvalidOperationException(full);
            var geometryMap = await spatial.CaptureAsync(items, 48, token);
            RequireScope(geometryMap.Scope, catalog);
            EntityGeometry machine = geometryMap.Prototypes[geometryMap.Items[machineItem].EntityName];
            bool transportAccess = input && output;
            FactoryZone? open = state.Zones.FirstOrDefault(z => z.TransportAccess == transportAccess
                && z.Pitch == FactoryBandPlanner.Pitch(machine, transportAccess)
                && z.BandHeight == FactoryBandPlanner.BandHeight(machine, transportAccess) && FactoryRegistry.NextSlot(state, z) is not null);
            zone = open ?? await CreateZoneAsync(machine, transportAccess);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var slot = FactoryRegistry.NextSlot(state, zone)!;
            cell = new($"cell-{Guid.NewGuid():N}", zone.Id, slot, kind, machineItem, recipe, new Dictionary<string, string>(), "building", geometryMap.CollectedTick);
            await registry.SaveAsync(state.With(cell), token);
        }

        var planningMap = await spatial.CaptureAsync(items, 48, token);
        RequireScope(planningMap.Scope, catalog);
        CellLayout layout = new FactoryBandPlanner().Layout(planningMap, equipment, zone.Origin, cell.Slot, input, output, zone.TransportAccess);
        // Recorded roles let maintenance rebuild a destroyed entity exactly where the cell expects it; power links
        // recorded by an interrupted run are kept.
        cell = cell with
        {
            Plan = layout.Entities.Concat((cell.Plan?.Values ?? []).Where(p => layout.Entities.All(e => e.Role != p.Role)))
                .ToDictionary(e => e.Role, StringComparer.Ordinal)
        };
        var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        RequireScope(known.Scope, catalog);
        cell = FactoryMaintenance.Reconcile(cell, known, catalog,
            state.Cells.Where(c => c.Id != cell.Id).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal), removeMissing: true);
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        await journal.AppendAsync("factory-cell-plan", new { cell.Id, kind, recipe, zone, layout }, token);
        await EnsureCarriedAsync(registry, catalog, CarriedStock.Unplaced(layout, cell.Entities), token);

        var walkway = new MapPosition((layout.Walkway.Min.X + layout.Walkway.Max.X) / 2, (layout.Walkway.Min.Y + layout.Walkway.Max.Y) / 2);
        await controller.TravelAsync(walkway, 1, catalog, token);
        await ClearAsync(layout);

        var ids = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        // The pole goes first so power problems are discovered before machines are committed.
        var context = new BuildContext(catalog, equipment, zone, controller, registry, items,
            map => KeepSteamGrowth(map, state.Zones));
        foreach (var planned in layout.Entities.OrderBy(e => e.Role == "pole" ? 0 : e.Role == "machine" ? 1 : 2))
        {
            if (!ids.ContainsKey(planned.Role))
            {
                ids[planned.Role] = await PlaceAsync(planned);
                cell = cell with { Entities = new Dictionary<string, string>(ids, StringComparer.Ordinal) };
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            }
            // Also on resume: a pole placed before an interrupted link may still be an unfed island.
            if (planned.Role == "pole")
                await ConnectPowerAsync(context, ids["pole"], planned.Position, token, async (linkId, link) =>
                {
                    // The link belongs to this cell so maintenance rebuilds it after an attack cuts the cell from the generators.
                    cell = WithLink(cell!, linkId, link, equipment.Pole);
                    foreach (var (role, id) in cell.Entities) ids[role] = id;
                    await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
                });
        }
        if (Configured(kind, recipe))
        {
            var configured = await controller.WorkAsync("set_recipe", new { entityId = ids["machine"], recipe }, 600, token: token);
            Completed(configured, "set_recipe");
        }
        var built = await spatial.CaptureAsync(items, 32, token);
        RequireScope(built.Scope, catalog);
        var machineEntity = built.Entities.SingleOrDefault(e => e.Id == ids["machine"])
            ?? throw new InvalidDataException("The built machine is not observed at its cell.");
        string probe = ids[PowerProbe(built.Prototypes[machineEntity.Name])];
        var consumer = built.Entities.Single(e => e.Id == probe);
        var poleEntity = built.Entities.Single(e => e.Id == ids["pole"]);
        if (consumer.Power?.NetworkId is null || consumer.Power.NetworkId != poleEntity.Power?.NetworkId)
            throw new InvalidOperationException("The cell's electric part is not on the cell pole's electric network.");
        if (FactoryPower.IsFed(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token), probe) == false)
            throw new InvalidOperationException("The cell network has no power source; its poles form an isolated island.");
        cell = cell with { Status = "ready", Tick = built.CollectedTick };
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        await journal.AppendAsync("factory-cell-ready", cell, token);
        return cell;

        SpatialSnapshot KeepSteamGrowth(SpatialSnapshot map, IReadOnlyList<FactoryZone> zones) => steam is null ? map
            : PowerExpansionController.ReserveGrowth(map, steam, zones, map.Entities.Single(e => e.Id == map.Actor.Id).Force);

        async Task<FactoryZone> CreateZoneAsync(EntityGeometry machine, bool transportAccess)
        {
            var map = await spatial.CaptureAsync(items, 48, token);
            RequireScope(map.Scope, catalog);
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(known.Scope, catalog);
            var powered = map.Entities.Where(e => map.Prototypes[e.Name].Type == "electric-pole" && e.Power?.NetworkId is not null
                    && FactoryPower.IsFed(known, e.Id) == true)
                .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).FirstOrDefault()
                ?? throw new InvalidOperationException("A factory zone needs a local pole on a known generator network; build or repair power first.");
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            // Empty slots still belong to their old zone. A new geometry must not allocate a second band over them.
            var ground = map;
            foreach (var existing in current.Zones) ground = ReserveZone(ground, existing, equipment.Pole);
            var site = new FactoryZonePlanner().FindLargest(KeepSteamGrowth(ground, current.Zones), machine, powered.Position, Slots(kind), token, transportAccess);
            if (site is null && steam is not null)
            {
                // A band that blocks steam growth is still better than no factory; the journal keeps the trade-off visible.
                await journal.AppendAsync("factory-zone-steam-growth-blocked", new { map.CollectedTick }, token);
                site = new FactoryZonePlanner().FindLargest(ground, machine, powered.Position, Slots(kind), token, transportAccess);
            }
            if (site is null) throw new InvalidOperationException("No dry, deposit-free rectangle for a factory band near the power network.");
            var created = new FactoryZone(current.Zones.Count == 0 ? 1 : current.Zones.Max(z => z.Id) + 1, site.Origin, site.Slots,
                FactoryBandPlanner.Pitch(machine, transportAccess), FactoryBandPlanner.BandHeight(machine, transportAccess), transportAccess);
            await registry.SaveAsync(current with { Zones = [.. current.Zones, created] }, token);
            await journal.AppendAsync("factory-zone", new { created, site.Clearance.Count, sourcePole = powered.Id, map.CollectedTick }, token);
            return created;
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

    }

    /// <summary>
    /// Protect keeps planned steam growth free when possible; links fall back to the plain reservation. Cells outside
    /// bands have no zone to reserve: their parts already stand when they are linked.
    /// </summary>
    internal sealed record BuildContext(ProductionCatalog Catalog, CellEquipment Equipment, FactoryZone? Zone,
        SpatialController Controller, FactoryRegistry Registry, string[] Items, Func<SpatialSnapshot, SpatialSnapshot> Protect);

    /// <summary>Joins unfed cell poles to a powered network, e.g. cells built before a failed link.</summary>
    public async Task<int> RepairPowerAsync(CancellationToken token = default)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        int repaired = 0;
        await using var controller = new SpatialController(game, journal);
        foreach (var cell in state.Cells.Where(c => c.Entities.ContainsKey("pole") && (c.Zone > 0 || c.Kind == "lab")))
        {
            if (FactoryPower.IsFed(snapshot, cell.Entities["pole"]) != false) continue;
            var equipment = Equipment(catalog, cell.MachineItem);
            equipment = equipment with { Pole = cell.Plan?.GetValueOrDefault("pole")?.Item ?? equipment.Pole };
            var position = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == cell.Entities["pole"])
                .Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            await controller.TravelAsync(position, 6, catalog, token);
            var context = new BuildContext(catalog, equipment, state.Zones.SingleOrDefault(z => z.Id == cell.Zone), controller, registry,
                [cell.MachineItem, equipment.Inserter, equipment.Chest, equipment.Pole, .. steam?.Machines ?? []],
                map => steam is null ? map : PowerExpansionController.ReserveGrowth(map, steam, state.Zones,
                    map.Entities.Single(e => e.Id == map.Actor.Id).Force));
            var repairedCell = cell;
            await ConnectPowerAsync(context, cell.Entities["pole"], position, token, async (linkId, link) =>
            {
                repairedCell = WithLink(repairedCell, linkId, link, equipment.Pole);
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(repairedCell), token);
            });
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            repaired++;
        }
        if (repaired > 0) await journal.AppendAsync("factory-power-repair", new { repaired }, token);
        repaired += await new ResourceCellBuilder(game, journal, directory).RepairPowerAsync(token);
        return repaired + await new FluidPowerRepair(game, journal, directory).RunAsync(token);
    }

    internal Task EnsureCarriedAsync(FactoryRegistry registry, ProductionCatalog catalog, string item, int count, CancellationToken token) =>
        count <= 0 ? Task.CompletedTask
            : EnsureCarriedAsync(registry, catalog, new Dictionary<string, int>(StringComparer.Ordinal) { [item] = count }, token);

    internal async Task EnsureCarriedAsync(FactoryRegistry registry, ProductionCatalog catalog, IReadOnlyDictionary<string, int> needed,
        CancellationToken token)
    {
        await new ResourceEquipmentReuse(game, journal).RecoverAsync(registry, catalog, needed, token);
        using (ProductionReservations.EnterFactory(await registry.LoadAsync(catalog.Scope.WorldId, token)))
            await CarriedStock.EnsureAsync(game, journal, catalog, needed, token);
    }

    internal async Task ConnectPowerAsync(BuildContext context, string poleId, MapPosition polePosition, CancellationToken token,
        Func<string, PlacementCandidate, Task> registerLink)
    {
        var spatial = new SpatialClient(game);
        for (int link = 0; link < 24; link++)
        {
            // Connectivity is proven on the whole known factory: a neighbouring cell pole may belong to the same unfed island.
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            bool? fed = FactoryPower.IsFed(snapshot, poleId);
            if (fed == true) return;
            var map = await spatial.CaptureAsync(context.Items, 48, token);
            RequireScope(map.Scope, context.Catalog);
            var pole = map.Entities.Single(e => e.Id == poleId);
            if (fed is null && pole.Power?.NetworkId is { } network && map.Entities.Any(e => e.Id != poleId && e.Power?.NetworkId == network
                && (map.Prototypes[e.Name].Type == "electric-pole" || IsPowerSource(map.Prototypes[e.Name].Type)))) return;
            var sources = map.Entities.Where(e => e.Force == pole.Force && e.Id != poleId
                && (fed is null || FactoryPower.IsFed(snapshot, e.Id) == true)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var reserved = context.Zone is null ? map : ReserveZone(map, context.Zone, context.Equipment.Pole);
            var next = new PowerGridPlanner().NextToPole(context.Protect(reserved), context.Equipment.Pole, pole.Name, pole.Position, sources, token);
            if (next.Status is PowerGridSearchStatus.NoObservedPath)
                next = new PowerGridPlanner().NextToPole(reserved, context.Equipment.Pole, pole.Name, pole.Position, sources, token);
            await journal.AppendAsync("factory-power-link", new { poleId, fed, next, map.CollectedTick }, token);
            if (next.Status == PowerGridSearchStatus.Connected && fed is null) return;
            if (next.Status != PowerGridSearchStatus.Extension || next.Pole is null)
                throw new InvalidOperationException($"The cell pole cannot join a powered network: {next.Status}.");
            // Link poles come from the carried stock for the whole planned chain; the cell's placed pole never stands in for them.
            await EnsureCarriedAsync(context.Registry, context.Catalog, context.Equipment.Pole, PowerGridPlanner.ChainPoles(next), token);
            string linkId = await new PoweredMachineController(game, journal).BuildAtAsync(context.Equipment.Pole, next.Pole, context.Catalog, context.Controller, token);
            await registerLink(linkId, next.Pole);
            await context.Controller.TravelAsync(polePosition, 6, context.Catalog, token);
        }
        throw new InvalidOperationException("Joining the cell to the electric network exceeded its link budget.");
    }

    /// <summary>Registers a power link pole under the next free <c>link-n</c> role, with its plan.</summary>
    public static FactoryCell WithLink(FactoryCell cell, string id, PlacementCandidate pole, string poleItem)
    {
        int index = 0;
        while (cell.Entities.ContainsKey($"link-{index}")) index++;
        string role = $"link-{index}";
        return cell with
        {
            Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [role] = id },
            Plan = new Dictionary<string, PlannedEntity>(cell.Plan ?? new Dictionary<string, PlannedEntity>(), StringComparer.Ordinal)
            {
                [role] = new(role, poleItem, pole.Position, pole.Direction)
            }
        };
    }

    /// <summary>Marks the whole band as occupied so power links never take a future cell's slot. A band is reserved once.</summary>
    public static SpatialSnapshot ReserveZone(SpatialSnapshot map, FactoryZone zone, string poleItem)
    {
        const string name = "factory-zone-reservation";
        if (map.Entities.Any(e => e.Id == $"{name}:{zone.Id}")) return map;
        var box = new WorldBox(zone.Origin, new(zone.Origin.X + zone.Slots * zone.Pitch, zone.Origin.Y + zone.BandHeight));

        var pole = map.Prototypes[map.Items[poleItem].EntityName];
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [name] = pole with { Name = name, Type = "reservation" } },
            // One id per zone: the collision field reports each obstacle id once, so shared ids would hide touching bands.
            Entities = [.. map.Entities, new SpatialEntity($"{name}:{zone.Id}", name,
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

    /// <summary>Whether the machine is given its recipe; furnaces select theirs from the ingredient they receive, a silo's is fixed.</summary>
    public static bool Configured(string kind, string? recipe) => recipe is not null && kind is not (FurnaceCellPlanner.Kind or SiloCellPlanner.Kind);

    /// <summary>Chest sides of a cell: laboratories take packs by hand, a silo consumes its ingredients without an item product.</summary>
    public static (bool Input, bool Output) Sides(string kind) => (kind != "lab", kind is not ("lab" or SiloCellPlanner.Kind));

    /// <summary>Slots of a new band: silos get a band of their own, sized for the one a factory builds.</summary>
    public static int Slots(string kind) => kind == SiloCellPlanner.Kind ? SiloCellPlanner.MaximumCells : ZoneSlots;

    /// <summary>
    /// Why a new cell of this kind is refused: silo cells in any status, a ready one whose silo is gone included, already fill the
    /// factory's <see cref="SiloCellPlanner.MaximumCells"/>. A band still has a free row beside its silo, so planning alone cannot stop it.
    /// </summary>
    public static string? Refusal(FactoryState state, string kind) =>
        kind == SiloCellPlanner.Kind && state.Cells.Count(c => c.Kind == kind) >= SiloCellPlanner.MaximumCells
            ? $"The factory already registers {SiloCellPlanner.MaximumCells} silo cell(s); resume or rebuild it instead of building another." : null;

    /// <summary>The role whose network proves the cell is powered: the machine, or the input inserter beside a burner machine.</summary>
    public static string PowerProbe(EntityGeometry machine) => machine.IsElectric ? "machine" : "input-inserter";

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
