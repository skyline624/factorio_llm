using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record NestClearingStep(string TargetId, string TargetName, IReadOnlyList<string> Turrets, double Exposure,
    int Destroyed, int Lost, int Recovered, string Outcome);
public sealed record NestClearingResult(string Status, MapPosition? Center, int Spawners, int Worms, int Destroyed,
    int TurretsLost, IReadOnlyList<NestClearingStep> Steps, long Tick);
public sealed record NestClearingAttempt(MapPosition Center, long Tick, string Outcome);
public sealed record NestClearingState(int Version, string WorldId, IReadOnlyList<NestClearingAttempt> Attempts, long? LastScoutTick = null);

/// <summary>
/// Clears one remembered enemy base near own industry by turret creep. Nothing offensive existed before: on seed 20261072
/// nests encircled the base, worms near the oil field closed every route to it (navigation keeps 24 tiles beyond worm
/// ranges) and 30 deaths followed trips through nest country. Turrets come from existing stock, are loaded with paid
/// magazines, fight from a bounded stand point, and survivors are mined back before the next step.
/// </summary>
public sealed class NestClearingController(IGameClient game, IControllerJournal journal, string directory)
{
    public const double ReachFromIndustry = 192;
    public const int TurretsPerStep = 4;
    public const int MagazinesPerTurret = 10;
    public const int MaximumSteps = 12;
    public const int MaximumLostTurrets = 8;
    public const double MaximumExposure = 15;
    /// <summary>Thirty game minutes before a base whose clearing failed is attempted again.</summary>
    public const long RetryTicks = 108000;
    private const long FightTicks = 3600;
    /// <summary>A scouting ring follows an attack seen within the last game hour, at most once per game hour.</summary>
    public const long ScoutWindowTicks = 216000;
    public const int ScoutPoints = 8;
    public const double ScoutMargin = 96;
    private const int MaximumLegs = 12;

    private string StatePath => Path.Combine(directory, "nest-clearing.json");

    public async Task<NestClearingResult> RunAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(25));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var safety = await SafetyAsync(token);
        if (!safety.Alive || safety.ControlMode != "ai") return await DoneAsync(new("unavailable", null, 0, 0, 0, 0, [], safety.Tick), token);
        if (safety.Enemies.Any(e => e.Type == "unit"))
            return await DoneAsync(new("deferred-enemies-near-actor", null, 0, 0, 0, 0, [], safety.Tick), token);
        var registry = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed before nest selection.");
        var industry = IndustryClusters.Read(registry, snapshot);
        var memory = await new NestMemoryStore(directory).LoadAsync(catalog.Scope.WorldId, token);
        var state = await LoadStateAsync(catalog.Scope.WorldId, token);
        // Unfinished cells mark where the factory is going: worms guarding a planned oil field block it before it stands.
        var planned = registry.Cells.Where(c => c.Status != "ready").SelectMany(c => c.Plan?.Values.Select(p => p.Position) ?? []).ToArray();
        var nest = Choose(memory, industry, state, safety.Tick, planned);
        if (nest is null)
        {
            // An attacking base the actor never saw cannot be chosen. On seed 20261106, 43 mining entities were destroyed at two
            // game hours by biters from an unseen nest; a player would go and look for it.
            var attacks = await new AttackLog(directory).LoadAsync(catalog.Scope.WorldId, token);
            bool attacked = attacks.Records.Any(r => safety.Tick - r.Tick <= ScoutWindowTicks && r.Destroyed + r.Damaged > 0);
            if (!attacked || industry.Count == 0 || state.LastScoutTick is { } last && safety.Tick - last < ScoutWindowTicks)
                return await DoneAsync(new("no-nest-in-reach", null, 0, 0, 0, 0, [], safety.Tick), token);
            state = state with { LastScoutTick = safety.Tick };
            await SaveStateAsync(state, token);
            await ScoutAsync(catalog, industry, attacks, async t => Choose(await new NestMemoryStore(directory).LoadAsync(catalog.Scope.WorldId, t),
                industry, state, safety.Tick, planned) is not null, token);
            nest = Choose(await new NestMemoryStore(directory).LoadAsync(catalog.Scope.WorldId, token), industry, state, safety.Tick, planned);
            if (nest is null) return await DoneAsync(new("scouted-no-nest", null, 0, 0, 0, 0, [], safety.Tick), token);
        }
        var result = await ExecuteAsync(catalog, registry, nest, safety.Tick, token);
        await SaveStateAsync(state with { Attempts = state.Attempts.Where(a => a.Center.DistanceTo(nest.Center) > NestMemory.LinkDistance)
            .Append(new NestClearingAttempt(nest.Center, result.Tick, result.Status)).TakeLast(64).ToArray() }, token);
        return await DoneAsync(result, token);
    }

    /// <summary>Clears the given remembered base with stocked turrets and magazines; selection and retry delays stay with the caller.</summary>
    internal async Task<NestClearingResult> ExecuteAsync(ProductionCatalog catalog, FactoryState registry, NestCluster nest, long tick,
        CancellationToken token)
    {
        var stock = SurvivalKitController.Stock(await new ProductionController(game, journal).ObserveAsync(token));
        string? turret = catalog.Turrets?.Keys.Where(k => stock.GetValueOrDefault(k) > 0 || FactoryDirector.Enabled(catalog, k))
            .OrderByDescending(k => stock.GetValueOrDefault(k)).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (turret is null) return new("no-turret", nest.Center, nest.Spawners, nest.Worms, 0, 0, [], tick);
        var choices = StrongestAmmunition(catalog.Turrets![turret], catalog, stock);
        if (choices.Count == 0) return new("no-ammunition", nest.Center, nest.Spawners, nest.Worms, 0, 0, [], tick);
        // The actor's own reserve stays loaded: the step's magazines come on top of the survival kit.
        long magazines = (long)TurretsPerStep * MagazinesPerTurret + SurvivalKitPlanner.MagazineReserve;
        string? ammunition = choices.FirstOrDefault(a => AttackResponseController.Affordable(catalog, turret, a,
            (int)(magazines / TurretsPerStep) + 1, stock, TurretsPerStep) == TurretsPerStep);
        if (ammunition is null) return new("no-stock", nest.Center, nest.Spawners, nest.Worms, 0, 0, [], tick);

        await journal.AppendAsync("nest-clearing-plan", new { nest.Center, nest.Spawners, nest.Worms, radius = nest.Radius, turret, ammunition }, token);
        using (ProductionReservations.EnterFactory(registry))
        {
            await new SurvivalKitController(game, journal).EnsureAsync("nest-clearing", token);
            return await ClearAsync(catalog, nest, turret, ammunition, token);
        }
    }

    /// <summary>
    /// The remembered base closest to own industry or to an unfinished cell's planned entity, within reach and not inside a
    /// retry delay after a failure.
    /// </summary>
    internal static NestCluster? Choose(NestMemory memory, IReadOnlyList<IndustryCluster> industry, NestClearingState state, long tick,
        IReadOnlyList<MapPosition>? planned = null)
    {
        planned ??= [];
        if (industry.Count == 0 && planned.Count == 0) return null;
        return memory.Clusters()
            .Where(c => c.Spawners + c.Worms > 0 && !state.Attempts.Any(a => a.Outcome != "cleared"
                && a.Center.DistanceTo(c.Center) <= NestMemory.LinkDistance && tick - a.Tick < RetryTicks))
            .Select(c => (Cluster: c, Distance: industry.Select(i => IndustryClusterPlanner.Distance(i.Box, c.Center))
                .Concat(planned.Select(p => p.DistanceTo(c.Center))).Min()))
            .Where(p => p.Distance <= ReachFromIndustry)
            .OrderBy(p => p.Distance).ThenBy(p => p.Cluster.Center.X).ThenBy(p => p.Cluster.Center.Y)
            .Select(p => p.Cluster).FirstOrDefault();
    }

    /// <summary>
    /// Compatible stocked or producible magazines, highest native round damage first: medium worms shrug off the cheapest
    /// rounds, which the defensive choice prefers.
    /// </summary>
    internal static IReadOnlyList<string> StrongestAmmunition(NativeTurret turret, ProductionCatalog catalog, IReadOnlyDictionary<string, long> stock) =>
        catalog.Items.Where(p => p.Value.AmmoCategory is { } category && turret.AmmoCategories.Contains(category)
                && p.Value.MagazineSize is > 0
                && (stock.GetValueOrDefault(p.Key) > 0 || catalog.Recipes.Any(r => r.Enabled && r.Ingredients.All(i => i.DeterministicItem)
                    && r.Products.Any(m => m.Name == p.Key && m.DeterministicItem))))
            .OrderByDescending(p => p.Value.RoundDamage ?? 0).ThenByDescending(p => stock.GetValueOrDefault(p.Key) > 0)
            .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray();

    /// <summary>
    /// Ring points around own industry, beyond it by <see cref="ScoutMargin"/>, starting toward the latest attack: each local
    /// photograph on the way enters the nest memory under normal visibility.
    /// </summary>
    internal static IReadOnlyList<MapPosition> ScoutRing(IReadOnlyList<IndustryCluster> industry, AttackLogState attacks)
    {
        double minX = industry.Min(i => i.Box.Min.X), minY = industry.Min(i => i.Box.Min.Y);
        double maxX = industry.Max(i => i.Box.Max.X), maxY = industry.Max(i => i.Box.Max.Y);
        var center = new MapPosition((minX + maxX) / 2, (minY + maxY) / 2);
        double radius = Math.Max(128, Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) / 2 + ScoutMargin);
        var latest = attacks.Records.Where(r => r.Points.Count > 0).OrderByDescending(r => r.Tick).FirstOrDefault();
        double start = latest is null ? 0 : Math.Atan2(latest.Points.Average(p => p.Y) - center.Y, latest.Points.Average(p => p.X) - center.X);
        return Enumerable.Range(0, ScoutPoints).Select(i =>
        {
            // Alternate sides of the attack bearing: 0, +45, -45, +90, -90 degrees and so on.
            int k = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
            double angle = start + k * 2 * Math.PI / ScoutPoints;
            return new MapPosition(Math.Round(center.X + radius * Math.Cos(angle)), Math.Round(center.Y + radius * Math.Sin(angle)));
        }).ToArray();
    }

    private async Task ScoutAsync(ProductionCatalog catalog, IReadOnlyList<IndustryCluster> industry, AttackLogState attacks,
        Func<CancellationToken, Task<bool>> found, CancellationToken token)
    {
        await new SurvivalKitController(game, journal).BeforeTripAsync("nest-scouting", token);
        await using var controller = new SpatialController(game, journal);
        foreach (var point in ScoutRing(industry, attacks))
        {
            try { await controller.TravelAsync(point, 8, catalog, token); }
            catch (Exception error) when (error is NavigationPlanningException or ExplorationDangerException
                || error is InvalidOperationException && error is not ObjectDisposedException)
            {
                await journal.AppendAsync("nest-scouting-leg-failed", new { point, error = error.GetType().Name, error.Message }, token);
                continue;
            }
            var view = await new SpatialClient(game).CaptureAsync(radius: 64, cancellationToken: token);
            await journal.AppendAsync("nest-scouting-view", new { point, view.Actor.Position, view.CollectedTick,
                structures = NestAssaultPlanner.Structures(view).Count }, token);
            if (await found(token)) return;
        }
    }

    /// <summary>A staging point toward the actor, beyond every remembered worm range plus the travel margin.</summary>
    internal static MapPosition Staging(NestCluster nest, MapPosition actor)
    {
        double range = nest.Structures.Select(s => s.Range ?? 0).DefaultIfEmpty(0).Max();
        double distance = nest.Radius + range + ExplorationPlanner.ThreatMargin + 2;
        double length = actor.DistanceTo(nest.Center);
        if (length < 1e-6) return new(nest.Center.X - distance, nest.Center.Y);
        return new(nest.Center.X + (actor.X - nest.Center.X) * distance / length, nest.Center.Y + (actor.Y - nest.Center.Y) * distance / length);
    }

    private async Task<NestClearingResult> ClearAsync(ProductionCatalog catalog, NestCluster nest, string turret, string ammunition,
        CancellationToken token)
    {
        await using var controller = new SpatialController(game, journal);
        var start = await new SpatialClient(game).CaptureAsync(radius: 16, cancellationToken: token);
        var staging = Staging(nest, start.Actor.Position);
        // Carry the first step's turrets and magazines before leaving: on seed 20261106 the actor reached the staging point,
        // then walked 150 tiles back to the base for turrets and returned.
        if (!await CarryAsync(turret, TurretsPerStep, ammunition, token))
            return new("no-stock", nest.Center, nest.Spawners, nest.Worms, 0, 0, [], (await SafetyAsync(token)).Tick);
        await controller.TravelAsync(staging, 6, catalog, token);
        var steps = new List<NestClearingStep>();
        int destroyed = 0, lost = 0, failedApproaches = 0;
        string status = "step-budget";
        for (int step = 0; step < MaximumSteps; step++)
        {
            var map = await new SpatialClient(game).CaptureAsync([turret], 64, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during nest clearing.");
            var remaining = Remaining(map, nest);
            if (remaining.Count == 0) { status = "cleared"; break; }
            var carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
            if (carried.GetValueOrDefault(turret) < TurretsPerStep
                || carried.GetValueOrDefault(ammunition) < (long)TurretsPerStep * MagazinesPerTurret + SurvivalKitPlanner.MagazineReserve)
            {
                if (!await CarryAsync(turret, TurretsPerStep, ammunition, token)) { status = "no-stock"; break; }
                await controller.TravelAsync(staging, 6, catalog, token);
                continue;
            }
            var plan = new NestAssaultPlanner().Plan(map with
            {
                Entities = map.Entities.Where(e => !IsHostile(map, e) || remaining.Any(r => r.Id == e.Id)).ToArray()
            }, turret, TurretsPerStep, token);
            if (plan is null) { status = "no-placement"; break; }
            if (plan.Exposure > MaximumExposure) { status = "too-exposed"; break; }
            await journal.AppendAsync("nest-clearing-step", new { step, plan, remaining = remaining.Count, map.CollectedTick }, token);
            NestClearingStep outcome;
            try { outcome = await StepAsync(controller, catalog, plan, staging, remaining, turret, ammunition, token); }
            catch (Exception error) when (error is NavigationPlanningException || error is InvalidOperationException && error is not ObjectDisposedException)
            {
                // Defense can build its own turrets on the planned stand point while a pack attacks: replan from a new photograph.
                await journal.AppendAsync("nest-clearing-approach-failed", new { step, error = error.GetType().Name, error.Message }, token);
                if (++failedApproaches >= 3) { status = "approach-failed"; break; }
                await MoveDirectAsync(controller, staging, token);
                continue;
            }
            failedApproaches = 0;
            steps.Add(outcome);
            destroyed += outcome.Destroyed;
            lost += outcome.Lost;
            if (lost >= MaximumLostTurrets) { status = "turret-losses"; break; }
            if (outcome.Outcome == "stalled" && outcome.Destroyed == 0) { status = "stalled"; break; }
        }
        long tick = (await SafetyAsync(token)).Tick;
        return new(status, nest.Center, nest.Spawners, nest.Worms, destroyed, lost, steps, tick);
    }

    private async Task<NestClearingStep> StepAsync(SpatialController controller, ProductionCatalog catalog, NestAssaultStep plan,
        MapPosition staging, IReadOnlyList<NestStructure> remaining, string turret, string ammunition, CancellationToken token)
    {
        await MoveDirectAsync(controller, plan.Stand, token);
        var placed = new List<string>();
        // Portable defense may spend the kit on packs met during the approach. On 2026-10-06 (seed 20261106) it used three of
        // four turrets and the lone remaining one was destroyed without a kill; a partial block now waits for a full kit.
        var bag = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory;
        if (bag.GetValueOrDefault(turret) < plan.Turrets.Count
            || bag.GetValueOrDefault(ammunition) < (long)plan.Turrets.Count * MagazinesPerTurret)
        {
            await MoveDirectAsync(controller, staging, token);
            var spent = new NestClearingStep(plan.Target.Id, plan.Target.Name, placed, plan.Exposure, 0, 0, 0, "kit-spent");
            await journal.AppendAsync("nest-clearing-step-result", spent, token);
            return spent;
        }
        foreach (var placement in plan.Turrets)
        {
            OperationReceipt built;
            try
            {
                built = await controller.WorkAsync("build", new { item = turret, position = placement.Position, direction = 0 }, 300, token: token);
            }
            catch (PlacementRefusedException) { break; }
            if (built.Status != "completed" || !built.Effects.TryGetProperty("entityId", out var id) || id.GetString() is not { } entityId) break;
            placed.Add(entityId);
        }
        foreach (string id in placed)
            await controller.WorkAsync("insert", new { entityId = id, inventory = "ammo", item = ammunition, count = MagazinesPerTurret }, 300, token: token);
        await MoveDirectAsync(controller, staging, token);
        var standing = placed.ToHashSet(StringComparer.Ordinal);
        string outcome = placed.Count == 0 ? "not-placed" : "stalled";
        for (long waited = 0; placed.Count > 0 && waited < FightTicks; waited += 120)
        {
            var wait = await controller.WorkAsync("wait", new { ticks = 120 }, 300, token: token);
            if (wait.Status != "completed") break;
            var view = await new SpatialClient(game).CaptureAsync([turret], 64, token);
            standing = placed.Where(id => view.Entities.Any(e => e.Id == id)).ToHashSet(StringComparer.Ordinal);
            if (standing.Count == 0) { outcome = "turrets-destroyed"; break; }
            var turretPositions = view.Entities.Where(e => standing.Contains(e.Id)).Select(e => e.Position).ToArray();
            bool targetStands = view.Entities.Any(e => e.Id == plan.Target.Id) || (view.StationaryThreats ?? []).Any(t => t.Id == plan.Target.Id);
            bool inRange = NestAssaultPlanner.Structures(view).Any(s => turretPositions.Any(p => p.DistanceTo(s.Position) <= Range(view, turret)));
            if (!targetStands && !inRange) { outcome = "target-destroyed"; break; }
        }
        var after = await new SpatialClient(game).CaptureAsync([turret], 64, token);
        var gone = NestAssaultPlanner.Structures(after).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        int destroyed = remaining.Count(r => !gone.Contains(r.Id) && after.Bounds.Contains(r.Position));
        int recovered = 0;
        var survivors = placed.Where(id => after.Entities.Any(e => e.Id == id)).ToArray();
        if (survivors.Length > 0) await MoveDirectAsync(controller, plan.Stand, token);
        foreach (string id in survivors)
        {
            var mined = await controller.WorkAsync("mine", new { entityId = id, count = 1 }, 900, token: token);
            if (mined.Status == "completed") recovered++;
        }
        if (survivors.Length > 0) await MoveDirectAsync(controller, staging, token);
        var result = new NestClearingStep(plan.Target.Id, plan.Target.Name, placed, plan.Exposure, destroyed,
            placed.Count - survivors.Length, recovered, outcome);
        await journal.AppendAsync("nest-clearing-step-result", result, token);
        return result;
    }

    /// <summary>
    /// A short native walk on a route that ignores worm envelopes: turret creep must enter a worm's reach to build, which
    /// the ordinary travel margin forbids. Collisions, terrain and the native 24-tile path limit still apply.
    /// </summary>
    private async Task MoveDirectAsync(SpatialController controller, MapPosition destination, CancellationToken token)
    {
        for (int leg = 0; leg < MaximumLegs; leg++)
        {
            var map = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
            if (map.Actor.Position.DistanceTo(destination) <= .6) return;
            var field = new SpatialCollisionField(map with { StationaryThreats = [] }, 0);
            // On 2026-10-06 (seed 20261106) a worm's stand lay beyond the 48-tile photograph after the spawner step and every
            // leg failed with GoalOutsideSnapshot; such a stand is approached through the line's farthest point still in view.
            bool beyond = !Inner(map.Bounds).Contains(destination);
            var goal = beyond ? Toward(map, destination) : destination;
            // A turret built by defense can now occupy the exact point; any point within reach of it serves the step.
            var route = new RoutePlanner().Find(field, goal, beyond ? 2 : .4, token: token);
            if (route.Status != RouteStatus.Found) route = new RoutePlanner().Find(field, goal, beyond ? 6 : 2, token: token);
            if (route.Status == RouteStatus.Found && route.Waypoints.Count == 0)
            {
                if (beyond) continue;
                return;
            }
            if (route.Status != RouteStatus.Found || route.Waypoints.Count == 0)
                throw new NavigationPlanningException(route.Status, $"{route.Status}: no direct nest approach route.");
            var path = new List<MapPosition>();
            double length = 0;
            var previous = map.Actor.Position;
            foreach (var point in SpatialController.Subdivide(map.Actor.Position, route.Waypoints))
            {
                double step = previous.DistanceTo(point);
                if (length + step > 23.5 || path.Count >= 60) break;
                path.Add(point);
                length += step;
                previous = point;
            }
            if (path.Count == 0) throw new NavigationPlanningException(RouteStatus.BudgetExceeded, "The direct nest approach has no first step.");
            var receipt = await controller.WorkAsync("move", new { position = path[^1], tolerance = .3, waypoints = path.Count > 1 ? path : null },
                900, new { position = map.Actor.Position, positionTolerance = .5 }, token);
            if (receipt.Status != "completed" && receipt.Error?.Code is not ("path_blocked" or "position_precondition" or "cancelled" or "deadline_exceeded"))
                throw new InvalidOperationException($"Direct nest approach ended with {receipt.Status}: {receipt.Error?.Code}.");
        }
        throw new NavigationPlanningException(RouteStatus.BudgetExceeded, "The direct nest approach exhausted its legs.");
    }

    private static WorldBox Inner(WorldBox bounds) => new(new(bounds.Min.X + 4, bounds.Min.Y + 4), new(bounds.Max.X - 4, bounds.Max.Y - 4));

    /// <summary>The farthest point toward the destination, at most 40 tiles away, that this photograph still shows with margin.</summary>
    internal static MapPosition Toward(SpatialSnapshot map, MapPosition destination)
    {
        var from = map.Actor.Position;
        double distance = from.DistanceTo(destination);
        var inner = Inner(map.Bounds);
        for (double reach = Math.Min(distance, 40); reach > 4; reach -= 2)
        {
            var point = new MapPosition(from.X + (destination.X - from.X) * reach / distance, from.Y + (destination.Y - from.Y) * reach / distance);
            if (inner.Contains(point)) return point;
        }
        throw new NavigationPlanningException(RouteStatus.GoalOutsideSnapshot, "The photograph shows no step toward the nest approach.");
    }

    private async Task<bool> CarryAsync(string turret, int turrets, string ammunition, CancellationToken token)
    {
        var executor = new ProductionGoalExecutor(game, journal);
        var carried = await executor.RunAsync(turret, turrets, token);
        if (carried.FinalStock < turrets) return false;
        int magazines = turrets * MagazinesPerTurret + SurvivalKitPlanner.MagazineReserve;
        return (await executor.RunAsync(ammunition, magazines, token)).FinalStock >= magazines;
    }

    private static IReadOnlyList<NestStructure> Remaining(SpatialSnapshot map, NestCluster nest) => NestAssaultPlanner.Structures(map)
        .Where(s => s.Position.DistanceTo(nest.Center) <= nest.Radius + NestMemory.LinkDistance).ToArray();

    private static bool IsHostile(SpatialSnapshot map, SpatialEntity entity) =>
        map.Prototypes[entity.Name].Type == "unit-spawner" || (map.StationaryThreats ?? []).Any(t => t.Id == entity.Id);

    private static double Range(SpatialSnapshot map, string turret) => map.Prototypes[map.Items[turret].EntityName].AttackRange ?? 0;

    private async Task<SafetyObservation> SafetyAsync(CancellationToken token) =>
        SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200, entityLimit = 1 }), token));

    private async Task<NestClearingResult> DoneAsync(NestClearingResult result, CancellationToken token)
    {
        await journal.AppendAsync("nest-clearing", result, token);
        return result;
    }

    private async Task<NestClearingState> LoadStateAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(StatePath)) return new(1, worldId, []);
        var state = JsonSerializer.Deserialize<NestClearingState>(await File.ReadAllTextAsync(StatePath, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty nest clearing state.");
        if (state.Version != 1 || state.WorldId != worldId) throw new InvalidDataException("Nest clearing state belongs to another world.");
        return state;
    }

    private Task SaveStateAsync(NestClearingState state, CancellationToken token) => LocalJson.WriteAsync(StatePath, state, token);
}
