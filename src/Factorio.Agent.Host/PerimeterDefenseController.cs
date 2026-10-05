using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record PerimeterTargets(IReadOnlyList<WorldBox> Core, IReadOnlyList<SpatialEntity> Optional);
/// <summary>
/// One ring (Cluster set) or the sum of every cluster ring (Rings set). NestsBuilt counts nests whose turret this run built;
/// Skipped explains a cluster that could not be ringed (planning refused, no proven route).
/// </summary>
public sealed record PerimeterDefenseResult(string TurretItem, string WallItem, string? Ammunition, int Layers, int Nests,
    int TurretsReady, int Walls, int Built, int Refused, int SkippedTurrets, int SkippedWalls, int CoverageGaps, double Spacing,
    bool CanLeave, bool CanEnter, IReadOnlyList<string> Unprotected, long StartTick, long EndTick, MaintenanceResult Upkeep,
    bool AlreadyComplete, IReadOnlyDictionary<string, long> Shortfall, string? Cluster = null, int NestsBuilt = 0,
    IReadOnlyList<PerimeterDefenseResult>? Rings = null, string? Skipped = null);

/// <summary>
/// Rings each cluster of known own industry with the nests of <see cref="PerimeterPlanner"/>: obtains turrets, walls and
/// ammunition through the production path, builds every turret before any wall, registers both as zone-0 factory cells so
/// maintenance rebuilds and rearms them, and loads the deployment reserve. A repeated run plans the same rings around their
/// registered entities and only builds what is missing; a complete ring is left alone. Owned entities outside every ring
/// are reported. On 2026-10-01 (seed 20261002) one ring around bands and distant resource rows exceeded one observation and
/// the whole perimeter was refused; each cluster now gets its own ring.
/// </summary>
public sealed class PerimeterDefenseController(IGameClient game, IControllerJournal journal, string directory)
{
    public const int Opening = 3;
    private sealed record Setup(ProductionCatalog Catalog, string TurretItem, NativeTurret Model, string WallItem, bool BuildWalls,
        string Ammunition, int MagazineSize);

    /// <summary>The strategic perimeter: every cluster ring, nearest first, walls included.</summary>
    public async Task<PerimeterDefenseResult> RunAsync(string wallItem, string? turretItem = null, int layers = 2, CancellationToken token = default)
    {
        if (layers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(layers));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        var setup = await PrepareAsync(wallItem, turretItem, true, token);
        await using var controller = new SpatialController(game, journal);
        // Preserve ammunition production demand without expanding unrelated factory targets before the defenses.
        await RegisterAmmunitionTargetAsync(setup, token);
        var known = await CaptureAsync(setup.Catalog, token);
        var clusters = IndustryClusters.Read(await new FactoryRegistry(directory).LoadAsync(setup.Catalog.Scope.WorldId, token), known);
        if (clusters.Count == 0) throw new InvalidOperationException("No known own industry to protect.");
        var actor = ActorPosition(known);
        var rings = new List<(PerimeterDefenseResult Result, WorldBox? Ring)>();
        foreach (var cluster in clusters.OrderBy(c => IndustryClusterPlanner.Distance(c.Box, actor)).ThenBy(c => c.Id, StringComparer.Ordinal))
            rings.Add(await RingAsync(setup, controller, cluster, [], int.MaxValue, layers, token));
        var planned = rings.Where(r => r.Ring is not null).ToArray();
        if (planned.Length == 0)
            throw new InvalidOperationException("No industry cluster could be ringed: " + string.Join("; ", rings.Select(r => r.Result.Skipped)));
        var final = await CaptureAsync(setup.Catalog, token);
        var defense = DefenseIds(await new FactoryRegistry(directory).LoadAsync(setup.Catalog.Scope.WorldId, token));
        var results = rings.Select(r => r.Result).ToArray();
        var shortfall = results.SelectMany(r => r.Shortfall).GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(p => p.Value), StringComparer.Ordinal);
        var result = new PerimeterDefenseResult(setup.TurretItem, wallItem, setup.Ammunition, layers, results.Sum(r => r.Nests),
            results.Sum(r => r.TurretsReady), results.Sum(r => r.Walls), results.Sum(r => r.Built), results.Sum(r => r.Refused),
            results.Sum(r => r.SkippedTurrets), results.Sum(r => r.SkippedWalls), results.Sum(r => r.CoverageGaps),
            planned.Max(r => r.Result.Spacing), planned.All(r => r.Result.CanLeave), planned.All(r => r.Result.CanEnter),
            Unprotected(Owned(final), defense, planned.Select(r => r.Ring!).ToArray()), known.CollectedTick, final.CollectedTick,
            planned[^1].Result.Upkeep, planned.All(r => r.Result.AlreadyComplete), shortfall, NestsBuilt: results.Sum(r => r.NestsBuilt),
            Rings: results);
        await journal.AppendAsync("perimeter-defense-result", result, token);
        return result;
    }

    /// <summary>
    /// An attack response on one cluster: its ring is planned whole, but at most <paramref name="maximumNests"/> unfinished nests
    /// are built, those covering the attacked points first, turrets before any wall. Walls are built only when
    /// <paramref name="buildWalls"/>: a missing wall technology never delays the turrets.
    /// </summary>
    public async Task<PerimeterDefenseResult> RunClusterAsync(IndustryCluster cluster, IReadOnlyList<MapPosition> focus, int maximumNests,
        string wallItem, bool buildWalls, string? turretItem = null, int layers = 2, CancellationToken token = default)
    {
        if (maximumNests < 1) throw new ArgumentOutOfRangeException(nameof(maximumNests));
        if (layers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(layers));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        token = deadline.Token;
        var setup = await PrepareAsync(wallItem, turretItem, buildWalls, token);
        await using var controller = new SpatialController(game, journal);
        await RegisterAmmunitionTargetAsync(setup, token);
        var (result, _) = await RingAsync(setup, controller, cluster, focus, maximumNests, layers, token);
        await journal.AppendAsync("perimeter-cluster-result", result, token);
        return result;
    }

    private async Task<Setup> PrepareAsync(string wallItem, string? turretItem, bool buildWalls, CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (!catalog.Items.TryGetValue(wallItem, out var wallPrototype) || wallPrototype.PlaceEntityType != "wall")
            throw new InvalidOperationException("Perimeters require a native wall item.");
        var carried = FactoryLogistics.Carried(await CaptureAsync(catalog, token));
        turretItem ??= catalog.Turrets?.Keys.OrderByDescending(k => carried.GetValueOrDefault(k) > 0)
            .ThenByDescending(k => FactoryDirector.Enabled(catalog, k)).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        NativeTurret? model = null;
        if (turretItem is null || catalog.Turrets?.TryGetValue(turretItem, out model) != true || catalog.Items[turretItem].PlaceEntity != model!.EntityName)
            throw new InvalidOperationException("Perimeters require a supported native ammunition turret.");
        var ammunition = DefenseDeploymentPlanner.ChooseAmmunition(model, catalog, carried);
        return new(catalog, turretItem, model, wallItem, buildWalls, ammunition, catalog.Items[ammunition].MagazineSize!.Value);
    }

    /// <summary>
    /// Retains ammunition demand for the next factory pass. Defense obtains paid stock through production without waiting
    /// for unrelated registered factory targets to be constructed; existing magazine cells remain available to logistics.
    /// </summary>
    private async Task RegisterAmmunitionTargetAsync(Setup setup, CancellationToken token)
    {
        var catalog = setup.Catalog;
        if (!FactoryDirector.Available(catalog)) return;
        var machines = FactoryDirector.MachinePreference.Where(m => FactoryDirector.Enabled(catalog, m)).Take(1).ToHashSet(StringComparer.Ordinal);
        if (AutomationPlanner.Choose(catalog, setup.Ammunition, machines) is null) return;
        // One turret reserve per minute; the actor still brings the plates.
        double perMinute = FactoryMaintenance.Magazines(0, setup.MagazineSize);
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        if (state.Targets?.GetValueOrDefault(setup.Ammunition) >= perMinute) return;
        await registry.SaveAsync(state.WithTarget(setup.Ammunition, perMinute), token);
        await journal.AppendAsync("perimeter-ammunition-target", new { setup.Ammunition, perMinute }, token);
    }

    /// <summary>Plans one cluster ring from its centre and builds its unfinished nests in priority order, at most the given count.</summary>
    private async Task<(PerimeterDefenseResult Result, WorldBox? Ring)> RingAsync(Setup setup, SpatialController controller, IndustryCluster cluster,
        IReadOnlyList<MapPosition> focus, int maximumNests, int layers, CancellationToken token)
    {
        bool wholeRing = maximumNests == int.MaxValue;
        var catalog = setup.Catalog;
        var registry = new FactoryRegistry(directory);
        var spatial = new SpatialClient(game);
        string turretItem = setup.TurretItem, wallItem = setup.WallItem;
        var initial = await CaptureAsync(catalog, token);
        var carried = FactoryLogistics.Carried(initial);
        await controller.TravelAsync(cluster.Center, 8, catalog, token);
        var map = await spatial.CaptureAsync([turretItem, wallItem], 48, token);
        RequireScope(map.Scope, catalog);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var defense = DefenseIds(state);
        var boxes = ClusterBoxes(cluster, state, map);
        PerimeterPlan plan;
        try
        {
            if (boxes.Count == 0) throw new InvalidOperationException("No member of the cluster is observed from its centre.");
            plan = new PerimeterPlanner().Plan(map, boxes, turretItem, wallItem, setup.Model.Range, layers, Opening, defense, token);
            if (!plan.CanLeave || !plan.CanEnter) throw new InvalidOperationException("The ring would not keep a proven route out of and into the cluster.");
        }
        catch (InvalidOperationException error)
        {
            // Planning is pure: nothing was built, and the other clusters are still ringed.
            await journal.AppendAsync("perimeter-cluster-skipped", new { cluster = cluster.Id, error.Message, map.CollectedTick }, token);
            var empty = new MaintenanceResult([], [], new Dictionary<string, long>(), new Dictionary<string, long>(), 0, map.CollectedTick, [], []);
            return (new(turretItem, wallItem, setup.Ammunition, layers, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, [], initial.CollectedTick,
                map.CollectedTick, empty, false, new Dictionary<string, long>(), cluster.Id, Skipped: error.Message), null);
        }
        await journal.AppendAsync("perimeter-plan", new { cluster = cluster.Id, turretItem, wallItem, layers, buildWalls = setup.BuildWalls,
            plan, map.CollectedTick }, token);
        var cells = plan.Nests.SelectMany(n => new[] { TurretCell(n), WallCell(n) }).ToHashSet(StringComparer.Ordinal);

        int built = 0, refused = 0, nestsBuilt = 0;
        var shortfall = new Dictionary<string, long>(StringComparer.Ordinal);
        var registered = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var observed = await CaptureAsync(catalog, token);
        var present = FactoryMaintenance.Present(observed);
        var unfinished = Prioritize(plan.Nests, focus, map.Actor.Position, setup.Model.Range)
            .Where(n => !Built(registered, present, TurretCell(n), n.Turret) || setup.BuildWalls && n.Walls.Any(w => !Built(registered, present, WallCell(n), w)))
            .Take(maximumNests).ToArray();
        bool complete = unfinished.Length == 0;
        if (complete) await journal.AppendAsync("perimeter-complete", new { cluster = cluster.Id, nests = plan.Nests.Count, observed.CollectedTick }, token);
        else
        {
            await ClearAsync(plan.Clearance.Where(e => unfinished.SelectMany(n => n.Walls.Prepend(n.Turret))
                .Any(p => Footprint(map, p).Overlaps(e.Bounds))).ToList());
            var remaining = Remaining(plan, registered, present).Where(r => unfinished.Any(n => TurretCell(n) == r.Cell || WallCell(n) == r.Cell))
                .GroupBy(p => p.Entity.Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            // Registered turrets keep their loaded rounds; only the missing reserve is produced. A bounded response only arms
            // the nests it builds, from stock its caller measured; the whole ring refills every nest.
            var rounds = DefenseFactoryState.Read(observed, catalog).Turrets.ToDictionary(t => t.Id, t => t.Rounds, StringComparer.Ordinal);
            await EnsureAsync(setup.Ammunition, (wholeRing ? plan.Nests : unfinished).Sum(n => FactoryMaintenance.Magazines(registered.Cells
                .SingleOrDefault(c => c.Id == TurretCell(n))?.Entities.GetValueOrDefault("turret") is { } id ? rounds.GetValueOrDefault(id) : 0, setup.MagazineSize)));
            await EnsureAsync(turretItem, remaining.GetValueOrDefault(turretItem));
            bool stocked = true;
            foreach (var nest in unfinished.TakeWhile(_ => stocked))
                stocked = await PlaceAsync(TurretCell(nest), "turret", turretItem, [nest.Turret], remaining);
            // Turrets fight while the walls go up.
            await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token, targetCellIds: cells);
            if (setup.BuildWalls)
            {
                if (stocked) await EnsureAsync(wallItem, remaining.GetValueOrDefault(wallItem));
                foreach (var nest in unfinished.Where(n => n.Walls.Count > 0).TakeWhile(_ => stocked))
                    stocked = await PlaceAsync(WallCell(nest), "wall", wallItem, nest.Walls, remaining);
            }
        }

        var upkeep = await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token, targetCellIds: cells);
        var final = await CaptureAsync(catalog, token);
        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var ids = state.Cells.Where(c => cells.Contains(c.Id)).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        int ready = DefenseFactoryState.Read(final, catalog).Turrets.Count(t => ids.Contains(t.Id) && DefenseDeploymentPlanner.Ready(t));
        present = FactoryMaintenance.Present(final);
        int walls = state.Cells.Where(c => c.Kind == "wall" && cells.Contains(c.Id)).Sum(c => c.Entities.Values.Count(present.Contains));
        var result = new PerimeterDefenseResult(turretItem, wallItem, setup.Ammunition, layers, plan.Nests.Count, ready, walls, built, refused,
            plan.Skipped.Count, plan.SkippedWalls, plan.CoverageGaps, plan.Spacing, plan.CanLeave, plan.CanEnter,
            Unprotected(Owned(final), DefenseIds(state), [plan.Ring]).Where(id => cluster.Members.Contains(id)).ToArray(),
            initial.CollectedTick, final.CollectedTick, upkeep, complete, shortfall, cluster.Id, nestsBuilt);
        return (result, plan.Ring);

        async Task ClearAsync(List<SpatialEntity> pending)
        {
            var position = map.Actor.Position;
            while (pending.Count > 0)
            {
                var obstacle = pending.OrderBy(e => e.Position.DistanceTo(position)).First();
                pending.Remove(obstacle);
                await controller.TravelAsync(obstacle.Position, 2, catalog, token);
                var local = await spatial.CaptureAsync(radius: 16, cancellationToken: token);
                RequireScope(local.Scope, catalog);
                position = local.Actor.Position;
                if (!local.Entities.Any(e => e.Id == obstacle.Id)) continue;
                var mined = await controller.WorkAsync("mine", new { name = obstacle.Name, position = obstacle.Position, count = 1 }, 1800, token: token);
                if (mined.Status is not ("completed" or "partial")) throw new InvalidOperationException($"Clearing {obstacle.Name} ended with {mined.Status}: {mined.Error?.Code}.");
                await journal.AppendAsync("perimeter-clearance", new { obstacle.Id, obstacle.Name, obstacle.Position }, token);
            }
        }

        async Task EnsureAsync(string item, int needed)
        {
            if (needed <= 0 || carried.GetValueOrDefault(item) >= needed) return;
            await new ProductionGoalExecutor(game, journal).RunAsync(item, Math.Min(1000, needed), token);
            carried = FactoryLogistics.Carried(await CaptureAsync(catalog, token));
        }

        // False when production could not deliver the item: the cell stays unfinished and nothing further is attempted.
        async Task<bool> PlaceAsync(string cellId, string kind, string item, IReadOnlyList<PlannedEntity> planned, Dictionary<string, int> remaining)
        {
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var nest = plan.Nests.Single(n => TurretCell(n) == cellId || WallCell(n) == cellId);
            var cell = (current.Cells.SingleOrDefault(c => c.Id == cellId) ?? new(cellId, 0, new(0, nest.Index, true), kind, item, null,
                new Dictionary<string, string>(), "building", map.CollectedTick)) with { Plan = planned.ToDictionary(e => e.Role, StringComparer.Ordinal) };
            var present = FactoryMaintenance.Present(await CaptureAsync(catalog, token));
            string? turretToArm = null;
            foreach (var entity in planned)
            {
                if (cell.Entities.TryGetValue(entity.Role, out var existing) && present.Contains(existing))
                {
                    if (kind == "turret") turretToArm = existing;
                    continue;
                }
                // Stock beyond one production batch is procured again when the bag runs out.
                if (carried.GetValueOrDefault(item) < 1) await EnsureAsync(item, remaining.GetValueOrDefault(item));
                if (carried.GetValueOrDefault(item) < 1)
                {
                    // A missing item is a shortage, never a refused placement.
                    shortfall[item] = Math.Max(1, remaining.GetValueOrDefault(item));
                    await journal.AppendAsync("perimeter-shortfall", new { cellId, entity, item, missing = shortfall[item] }, token);
                    return false;
                }
                remaining[item] = remaining.GetValueOrDefault(item) - 1;
                string id;
                try { id = await new PoweredMachineController(game, journal).BuildAtAsync(item, new(entity.Position, entity.Direction, 0), catalog, controller, token); }
                catch (PlacementRefusedException error)
                {
                    // Only a native or geometric refusal is skipped: the world is unchanged and a later run plans it again.
                    // Manual control, lease loss, travel and receipt failures propagate and leave the cell as last saved.
                    refused++;
                    await journal.AppendAsync("perimeter-placement-refused", new { cellId, entity, error.Message }, token);
                    continue;
                }
                built++;
                if (kind == "turret") nestsBuilt++;
                if (kind == "turret") turretToArm = id;
                carried[item] = carried.GetValueOrDefault(item) - 1;
                cell = cell with { Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [entity.Role] = id } };
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            }
            if (cell.Entities.Count > 0) cell = cell with { Status = "ready" };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            await journal.AppendAsync("perimeter-cell", cell, token);
            // Each nest must defend the next placement; a distant ring may be attacked before its last turret exists.
            if (turretToArm is not null) return await ArmAsync(turretToArm);
            return true;
        }

        async Task<bool> ArmAsync(string turretId)
        {
            var stock = await new FactorySnapshotClient(game).CaptureAsync([setup.Ammunition], cancellationToken: token);
            RequireScope(stock.Scope, catalog);
            var turret = DefenseFactoryState.Read(stock, catalog).Turrets.Single(t => t.Id == turretId);
            if (!turret.Active || turret.Name != setup.Model.EntityName
                || turret.Ammunition is not null && turret.Ammunition != setup.Ammunition)
                throw new InvalidDataException("The perimeter turret changed before its immediate supply.");
            long need = FactoryMaintenance.Magazines(turret.Rounds, setup.MagazineSize);
            if (need == 0) return true;
            carried = FactoryLogistics.Carried(stock);
            var inventory = stock.Records.Single(r => r.Id == turret.InventoryId && r.Kind == "inventory");
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(setup.Ammunition).GetProperty("insertable").GetInt64();
            int give = checked((int)Math.Min(need, Math.Min(capacity, carried.GetValueOrDefault(setup.Ammunition))));
            long moved = give > 0 ? await FactoryMaintenance.TransferTurretAmmunitionAsync(controller, catalog, turret,
                setup.Ammunition, give, journal, token) : 0;
            carried[setup.Ammunition] = carried.GetValueOrDefault(setup.Ammunition) - moved;
            await journal.AppendAsync("perimeter-turret-armed", new { turretId, ammunition = setup.Ammunition,
                stock.SnapshotId, stock.CollectedTick, beforeRounds = turret.Rounds, requested = give, transferred = moved }, token);
            if (moved >= need) return true;
            shortfall[setup.Ammunition] = Math.Max(shortfall.GetValueOrDefault(setup.Ammunition), need - moved);
            return false; // Preserve the registered turret and stop extending this ring without its reserve.
        }
    }

    /// <summary>
    /// Nests in build order. After an attack, those whose turret covers the most attacked points come first, then the nearest
    /// to them; otherwise the ring is built going round from the nest nearest the actor.
    /// </summary>
    public static IReadOnlyList<PerimeterNest> Prioritize(IReadOnlyList<PerimeterNest> nests, IReadOnlyList<MapPosition> focus, MapPosition actor, double range)
    {
        if (nests.Count == 0) return [];
        if (focus.Count == 0)
        {
            int start = nests.OrderBy(n => n.Turret.Position.DistanceTo(actor)).First().Index;
            return nests.SkipWhile(n => n.Index != start).Concat(nests.TakeWhile(n => n.Index != start)).ToArray();
        }
        return nests.OrderByDescending(n => focus.Count(p => p.DistanceTo(n.Turret.Position) <= range))
            .ThenBy(n => focus.Min(p => p.DistanceTo(n.Turret.Position))).ThenBy(n => n.Index).ToArray();
    }

    /// <summary>Bands of the cluster and the observed footprints of its member entities: the protected core of its ring.</summary>
    public static IReadOnlyList<WorldBox> ClusterBoxes(IndustryCluster cluster, FactoryState state, SpatialSnapshot map)
    {
        var members = cluster.Members.ToHashSet(StringComparer.Ordinal);
        return state.Zones.Where(z => members.Contains($"zone-{z.Id}")).Select(z => z.Box)
            .Concat(map.Entities.Where(e => members.Contains(e.Id)).Select(e => e.Bounds)).ToArray();
    }

    /// <summary>Cells and bands are the core; other owned industry (the shared defense set, silos and drills included) is optional.</summary>
    public static PerimeterTargets Targets(FactoryState state, IReadOnlySet<string> owned, SpatialSnapshot map)
    {
        var defense = DefenseIds(state);
        var cellIds = state.Cells.Where(c => !IsDefense(c)).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var core = state.Zones.Select(z => z.Box).Concat(map.Entities.Where(e => cellIds.Contains(e.Id)).Select(e => e.Bounds)).ToArray();
        var optional = map.Entities.Where(e => owned.Contains(e.Id) && !cellIds.Contains(e.Id) && !defense.Contains(e.Id)
            && DefenseFactoryState.Industry.Contains(map.Prototypes[e.Name].Type)).ToArray();
        return new(core, optional);
    }

    /// <summary>Every owned entity outside the turret line, whatever its type, except the registered defenses themselves.</summary>
    public static IReadOnlyList<string> Unprotected(IReadOnlyDictionary<string, MapPosition> owned, IReadOnlySet<string> defense, WorldBox ring) =>
        Unprotected(owned, defense, [ring]);

    /// <summary>Every owned entity outside all turret lines, whatever its type, except the registered defenses themselves.</summary>
    public static IReadOnlyList<string> Unprotected(IReadOnlyDictionary<string, MapPosition> owned, IReadOnlySet<string> defense, IReadOnlyList<WorldBox> rings) =>
        owned.Where(p => !defense.Contains(p.Key) && !rings.Any(r => r.Contains(p.Value))).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Planned entities whose role is not registered in its cell or whose registered entity is gone.</summary>
    public static IReadOnlyList<(string Cell, PlannedEntity Entity)> Remaining(PerimeterPlan plan, FactoryState state, IReadOnlySet<string> present) =>
        plan.Nests.SelectMany(n => n.Walls.Select(w => (Cell: WallCell(n), Entity: w)).Prepend((Cell: TurretCell(n), Entity: n.Turret)))
            .Where(p => !Built(state, present, p.Cell, p.Entity)).ToArray();

    /// <summary>The registered ring already covers this plan: every planned entity is present and every cell finished.</summary>
    public static bool Complete(PerimeterPlan plan, FactoryState state, IReadOnlySet<string> present) =>
        Remaining(plan, state, present).Count == 0 && plan.Nests
            .SelectMany(n => n.Walls.Count > 0 ? new[] { TurretCell(n), WallCell(n) } : [TurretCell(n)])
            .All(id => state.Cells.Any(c => c.Id == id && c.Status == "ready"));

    private static bool Built(FactoryState state, IReadOnlySet<string> present, string cell, PlannedEntity entity) =>
        state.Cells.SingleOrDefault(c => c.Id == cell) is { } registered && registered.Entities.TryGetValue(entity.Role, out var id) && present.Contains(id);

    private static bool IsDefense(FactoryCell cell) => cell.Kind is "turret" or "wall";

    private static HashSet<string> DefenseIds(FactoryState state) =>
        state.Cells.Where(IsDefense).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);

    public static string TurretCell(PerimeterNest nest) => "perimeter-turret@" + Key(nest.Turret.Position);
    public static string WallCell(PerimeterNest nest) => "perimeter-wall@" + Key(nest.Turret.Position);
    private static string Key(MapPosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.X:0.#},{position.Y:0.#}");

    private static WorldBox Footprint(SpatialSnapshot map, PlannedEntity entity) =>
        map.Prototypes[map.Items[entity.Item].EntityName].CollisionBox.Rotate(entity.Direction).Translate(entity.Position);

    private async Task<FactorySnapshot> CaptureAsync(ProductionCatalog catalog, CancellationToken token)
    {
        var value = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        RequireScope(value.Scope, catalog);
        return value;
    }

    private static Dictionary<string, MapPosition> Owned(FactorySnapshot snapshot) => snapshot.Records
        .Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory")
        .ToDictionary(r => r.EntityId, r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, StringComparer.Ordinal);

    private static MapPosition ActorPosition(FactorySnapshot snapshot) => snapshot.Records
        .Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor").Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building the perimeter; reconcile partial construction.");
    }
}
