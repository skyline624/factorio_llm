using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record PerimeterTargets(IReadOnlyList<WorldBox> Core, IReadOnlyList<SpatialEntity> Optional, IReadOnlyList<string> Unobserved);
public sealed record PerimeterDefenseResult(string TurretItem, string WallItem, string? Ammunition, int Layers, int Nests,
    int TurretsReady, int Walls, int Built, int Refused, int SkippedTurrets, int SkippedWalls, int CoverageGaps, double Spacing,
    bool CanLeave, bool CanEnter, IReadOnlyList<string> Unprotected, long StartTick, long EndTick, MaintenanceResult Upkeep);

/// <summary>
/// Rings the known factory with the nests of <see cref="PerimeterPlanner"/>: obtains turrets, walls and ammunition through
/// the production path, builds every turret before any wall, registers both as zone-0 factory cells so maintenance
/// rebuilds and rearms them, and loads the deployment reserve. Industry beyond one observed area stays unprotected.
/// </summary>
public sealed class PerimeterDefenseController(IGameClient game, IControllerJournal journal, string directory)
{
    public const int Opening = 3;
    private static readonly HashSet<string> Industry = new(StringComparer.Ordinal)
        { "furnace", "boiler", "generator", "offshore-pump", "electric-energy-interface", "solar-panel", "burner-generator", "assembling-machine", "lab" };

    public async Task<PerimeterDefenseResult> RunAsync(string wallItem, string? turretItem = null, int layers = 2, CancellationToken token = default)
    {
        if (layers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(layers));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (!catalog.Items.TryGetValue(wallItem, out var wallPrototype) || wallPrototype.PlaceEntityType != "wall")
            throw new InvalidOperationException("Perimeters require a native wall item.");
        var snapshots = new FactorySnapshotClient(game);
        var spatial = new SpatialClient(game);
        var registry = new FactoryRegistry(directory);
        var initial = await CaptureAsync();
        var carried = FactoryLogistics.Carried(initial);
        turretItem ??= catalog.Turrets?.Keys.OrderByDescending(k => carried.GetValueOrDefault(k) > 0)
            .ThenByDescending(k => FactoryDirector.Enabled(catalog, k)).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        NativeTurret? model = null;
        if (turretItem is null || catalog.Turrets?.TryGetValue(turretItem, out model) != true || catalog.Items[turretItem].PlaceEntity != model!.EntityName)
            throw new InvalidOperationException("Perimeters require a supported native ammunition turret.");
        var ammunition = DefenseDeploymentPlanner.ChooseAmmunition(model, catalog, carried);
        int magazineSize = catalog.Items[ammunition].MagazineSize!.Value;
        await using var controller = new SpatialController(game, journal);
        // Before planning, so a band created for the magazine cell lies inside the ring.
        await AutomateAmmunitionAsync();

        // Observe the whole ring from the factory core: cells and bands first, otherwise known power and smelting.
        var known = await CaptureAsync(); // Automation may have added cells and spent carried items.
        carried = FactoryLogistics.Carried(known);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var owned = Owned(known);
        var cellIds = state.Cells.Where(c => !IsDefense(c)).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var anchors = state.Zones.Select(z => Center(z.Box)).Concat(owned.Where(p => cellIds.Contains(p.Key)).Select(p => p.Value)).ToArray();
        if (anchors.Length == 0) anchors = known.Records.Where(r => owned.ContainsKey(r.EntityId) && Industry.Contains(r.Data.GetProperty("type").GetString()!))
            .Select(r => owned[r.EntityId]).ToArray();
        if (anchors.Length == 0) throw new InvalidOperationException("No known own industry to protect.");
        await controller.TravelAsync(new(anchors.Average(p => p.X), anchors.Average(p => p.Y)), 8, catalog, token);
        var map = await spatial.CaptureAsync([turretItem, wallItem], 48, token);
        RequireScope(map.Scope, catalog);
        var targets = Targets(state, owned.Keys.ToHashSet(StringComparer.Ordinal), map);
        var included = PerimeterPlanner.Select(map, targets.Core, targets.Optional.Select(e => e.Bounds).ToArray(), turretItem, layers, Opening);
        var boxes = targets.Core.Concat(included.Select(i => targets.Optional[i].Bounds)).ToArray();
        var unprotected = targets.Optional.Where((_, i) => !included.Contains(i)).Select(e => e.Id).Concat(targets.Unobserved).ToArray();
        if (boxes.Length == 0) throw new InvalidOperationException("No known industry lies within one observed perimeter.");
        var plan = new PerimeterPlanner().Plan(map, boxes, turretItem, wallItem, model.Range, layers, Opening, token);
        await journal.AppendAsync("perimeter-plan", new { turretItem, wallItem, layers, plan, unprotected, map.CollectedTick }, token);
        if (!plan.CanLeave || !plan.CanEnter) throw new InvalidOperationException("The perimeter would not keep a proven route out of and into the factory.");

        await ClearAsync();
        int built = 0, refused = 0;
        var start = plan.Nests.OrderBy(n => n.Turret.Position.DistanceTo(map.Actor.Position)).First().Index;
        var ordered = plan.Nests.Skip(start).Concat(plan.Nests.Take(start)).ToArray();
        var remaining = await RemainingAsync();
        await EnsureAsync(ammunition, ordered.Length * FactoryMaintenance.Magazines(0, magazineSize));
        await EnsureAsync(turretItem, remaining.GetValueOrDefault(turretItem));
        foreach (var nest in ordered)
            await PlaceAsync(TurretCell(nest), "turret", turretItem, [nest.Turret]);
        // Turrets fight while the walls go up.
        await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token);
        await EnsureAsync(wallItem, remaining.GetValueOrDefault(wallItem));
        foreach (var nest in ordered)
            await PlaceAsync(WallCell(nest), "wall", wallItem, nest.Walls);

        var upkeep = await new FactoryMaintenance(game, journal, directory).RunAsync(controller, catalog, token);
        var final = await CaptureAsync();
        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var cells = plan.Nests.SelectMany(n => new[] { TurretCell(n), WallCell(n) }).ToHashSet(StringComparer.Ordinal);
        var ids = state.Cells.Where(c => cells.Contains(c.Id)).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        int ready = DefenseFactoryState.Read(final, catalog).Turrets.Count(t => ids.Contains(t.Id) && DefenseDeploymentPlanner.Ready(t));
        var present = FactoryMaintenance.Present(final);
        int walls = state.Cells.Where(c => c.Kind == "wall" && cells.Contains(c.Id)).Sum(c => c.Entities.Values.Count(present.Contains));
        var result = new PerimeterDefenseResult(turretItem, wallItem, ammunition, layers, plan.Nests.Count, ready, walls, built, refused,
            plan.Skipped.Count, plan.SkippedWalls, plan.CoverageGaps, plan.Spacing, plan.CanLeave, plan.CanEnter, unprotected,
            initial.CollectedTick, final.CollectedTick, upkeep);
        await journal.AppendAsync("perimeter-defense-result", result, token);
        return result;

        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await snapshots.CaptureAsync(cancellationToken: token);
            RequireScope(value.Scope, catalog);
            return value;
        }

        // A persistent magazine cell resupplies turrets through logistics once assemblers are available.
        async Task AutomateAmmunitionAsync()
        {
            if (!FactoryDirector.Available(catalog)) return;
            var machines = FactoryDirector.MachinePreference.Where(m => FactoryDirector.Enabled(catalog, m)).Take(1).ToHashSet(StringComparer.Ordinal);
            if (AutomationPlanner.Choose(catalog, ammunition, machines) is null) return;
            try
            {
                // One turret reserve per minute; the actor still brings the plates.
                await new FactoryDirector(game, journal, directory).AutomateAsync(ammunition, FactoryMaintenance.Magazines(0, magazineSize), token);
            }
            catch (InvalidOperationException error)
            {
                // An unfinished cell keeps its slot and resumes later; hand production still supplies this perimeter.
                await journal.AppendAsync("perimeter-ammunition-automation-unavailable", new { ammunition, error.Message }, token);
            }
        }

        async Task<Dictionary<string, int>> RemainingAsync()
        {
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var present = FactoryMaintenance.Present(await CaptureAsync());
            bool Built(string cellId, string role) => current.Cells.SingleOrDefault(c => c.Id == cellId)?.Entities.TryGetValue(role, out var id) == true
                && present.Contains(id);
            return plan.Nests.SelectMany(n => n.Walls.Select(w => (Cell: WallCell(n), Entity: w)).Append((Cell: TurretCell(n), Entity: n.Turret)))
                .Where(p => !Built(p.Cell, p.Entity.Role)).GroupBy(p => p.Entity.Item).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        }

        async Task ClearAsync()
        {
            var position = map.Actor.Position;
            var pending = plan.Clearance.ToList();
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
            carried = FactoryLogistics.Carried(await CaptureAsync());
        }

        async Task PlaceAsync(string cellId, string kind, string item, IReadOnlyList<PlannedEntity> planned)
        {
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var nest = plan.Nests.Single(n => TurretCell(n) == cellId || WallCell(n) == cellId);
            var cell = (current.Cells.SingleOrDefault(c => c.Id == cellId) ?? new(cellId, 0, new(0, nest.Index, true), kind, item, null,
                new Dictionary<string, string>(), "building", map.CollectedTick)) with { Plan = planned.ToDictionary(e => e.Role, StringComparer.Ordinal) };
            var present = FactoryMaintenance.Present(await CaptureAsync());
            foreach (var entity in planned)
            {
                if (cell.Entities.TryGetValue(entity.Role, out var existing) && present.Contains(existing)) continue;
                // Stock beyond one production batch is procured again when the bag runs out.
                if (carried.GetValueOrDefault(item) < 1) await EnsureAsync(item, remaining.GetValueOrDefault(item));
                remaining[item] = remaining.GetValueOrDefault(item) - 1;
                string id;
                try { id = await new PoweredMachineController(game, journal).BuildAtAsync(item, new(entity.Position, entity.Direction, 0), catalog, controller, token); }
                catch (InvalidOperationException error)
                {
                    // Placement is validated natively before submission; a refusal leaves the world unchanged.
                    refused++;
                    await journal.AppendAsync("perimeter-placement-refused", new { cellId, entity, error.Message }, token);
                    continue;
                }
                built++;
                carried[item] = carried.GetValueOrDefault(item) - 1;
                cell = cell with { Entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal) { [entity.Role] = id } };
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            }
            if (cell.Entities.Count > 0) cell = cell with { Status = "ready" };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            await journal.AppendAsync("perimeter-cell", cell, token);
        }
    }

    /// <summary>Cells and bands are the core; other owned power, smelting and machines are optional extensions.</summary>
    public static PerimeterTargets Targets(FactoryState state, IReadOnlySet<string> owned, SpatialSnapshot map)
    {
        var defense = state.Cells.Where(IsDefense).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var cellIds = state.Cells.Where(c => !IsDefense(c)).SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var core = state.Zones.Select(z => z.Box).Concat(map.Entities.Where(e => cellIds.Contains(e.Id)).Select(e => e.Bounds)).ToArray();
        var optional = map.Entities.Where(e => owned.Contains(e.Id) && !cellIds.Contains(e.Id) && !defense.Contains(e.Id)
            && Industry.Contains(map.Prototypes[e.Name].Type)).ToArray();
        var unobserved = cellIds.Where(id => owned.Contains(id) && !map.Entities.Any(e => e.Id == id)).Order(StringComparer.Ordinal).ToArray();
        return new(core, optional, unobserved);
    }

    private static bool IsDefense(FactoryCell cell) => cell.Kind is "turret" or "wall";

    private static string TurretCell(PerimeterNest nest) => "perimeter-turret@" + Key(nest.Turret.Position);
    private static string WallCell(PerimeterNest nest) => "perimeter-wall@" + Key(nest.Turret.Position);
    private static string Key(MapPosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.X:0.#},{position.Y:0.#}");

    private static Dictionary<string, MapPosition> Owned(FactorySnapshot snapshot) => snapshot.Records
        .Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory")
        .ToDictionary(r => r.EntityId, r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, StringComparer.Ordinal);

    private static MapPosition Center(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while building the perimeter; reconcile partial construction.");
    }
}
