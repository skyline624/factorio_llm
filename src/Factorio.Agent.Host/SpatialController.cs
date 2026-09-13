using System.Net.Sockets;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ExplorationWaypoint(MapPosition Position, long CollectedTick);

/// <summary>Executes C# spatial plans while yielding actor operations to the deterministic defense loop.</summary>
public sealed class SpatialController(IGameClient game, IControllerJournal journal) : IAsyncDisposable
{
    private readonly SpatialClient spatial = new(game);
    private readonly OperationClient operations = new(game);
    private readonly DefenseController defense = new(game, journal);
    private string? ownedOperation;

    public async Task ApproachEntityAsync(string entityId, MapPosition knownPosition, ProductionCatalog catalog,
        CancellationToken token = default)
    {
        SpatialSnapshot map = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while approaching an entity.");
        if (!map.Entities.Any(e => e.Id == entityId))
        {
            await TravelAsync(knownPosition, 8, catalog, token);
            map = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while approaching an entity.");
        }
        var entity = map.Entities.Single(e => e.Id == entityId);
        MapPosition approach = new PlacementPlanner().FindInteractionApproach(new(map), entity)
            ?? throw new InvalidOperationException("No reachable interaction position for the observed entity.");
        // Interaction geometry is observed over 48 tiles; navigation uses smaller local snapshots.
        await TravelAsync(approach, .2, catalog, token);
    }

    public async Task TravelAsync(MapPosition destination, double arrivalDistance, ProductionCatalog catalog,
        CancellationToken token = default)
    {
        var exploration = new ExplorationPlanner();
        for (int segment = 0; segment < 64; segment++)
        {
            SpatialSnapshot map = await spatial.CaptureAsync(cancellationToken: token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during travel to a known destination.");
            if (map.Actor.Position.DistanceTo(destination) <= 24)
            {
                await NavigateAsync(destination, arrivalDistance, token);
                return;
            }
            ExplorationWaypoint next = await FindExplorationWaypointAsync(exploration, catalog, "", destination, token);
            await NavigateAsync(next.Position, cancellationToken: token);
        }
        throw new InvalidOperationException("Travel exhausted its local segment budget.");
    }

    public async Task<NavigationResult> NavigateAsync(MapPosition destination, double arrivalDistance = 0.4,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(arrivalDistance) || arrivalDistance is < 0.2 or > 10)
            throw new ArgumentOutOfRangeException(nameof(arrivalDistance));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var receipts = new List<OperationReceipt>();
        var remaining = new List<MapPosition>();
        int plans = 0, clearedTrees = 0;
        SpatialSnapshot initial = await spatial.CaptureAsync(cancellationToken: deadline.Token);
        RequireAi(initial);
        ActorScope scope = initial.Scope;
        while (plans < 256)
        {
            deadline.Token.ThrowIfCancellationRequested();
            DefenseStep reflex = await DefenseStepAsync(deadline.Token);
            if (reflex.State is "defending" or "preempted" or "reconciled" or "uncertain")
            {
                await Task.Delay(150, deadline.Token);
                continue;
            }
            SpatialSnapshot map;
            try { map = await spatial.CaptureAsync(cancellationToken: deadline.Token); }
            catch (GameRpcException error) when (error.Error.Code == "actor_dead")
            {
                throw new InvalidDataException("The actor died during navigation; reconcile before choosing a new route.", error);
            }
            if (map.Scope != scope) throw new InvalidDataException("Actor scope changed during navigation; the previous route is no longer executable.");
            RequireAi(map);
            if (map.Actor.Position.DistanceTo(destination) <= arrivalDistance)
                return new(destination, map.Actor.Position, arrivalDistance, plans, receipts.AsReadOnly());
            var field = new SpatialCollisionField(map);
            while (remaining.Count > 0 && map.Actor.Position.DistanceTo(remaining[0]) <= 0.15) remaining.RemoveAt(0);
            bool reusable = remaining.Count > 0 && field.SegmentClear(map.Actor.Position, remaining[0], 0);
            RoutePlan route = reusable
                ? new(RouteStatus.Found, remaining.ToArray(), 0, 0)
                : new RoutePlanner().Find(field, destination, Math.Max(0, arrivalDistance - 0.2), token: deadline.Token);
            if (!reusable && route.Status == RouteStatus.Found)
                route = route with { Waypoints = Subdivide(map.Actor.Position, route.Waypoints) };
            plans++;
            await journal.AppendAsync("route-plan", new { map.Scope, map.CollectedTick, destination, arrivalDistance,
                map.StationaryThreats, reused = reusable, route }, deadline.Token);
            if (route.Status == RouteStatus.NoRouteOnKnownGrid && clearedTrees < 16)
            {
                ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), deadline.Token));
                if (catalog.Scope != map.Scope) throw new InvalidDataException("Actor changed before clearing the route.");
                if (await ClearTreeAsync(map, catalog, destination, deadline.Token))
                {
                    clearedTrees++;
                    remaining.Clear();
                    continue;
                }
            }
            if (route.Status != RouteStatus.Found || route.Waypoints.Count == 0)
                throw new NavigationPlanningException(route.Status, $"{route.Status}: no executable route under the current snapshot and search budget.");
            MapPosition waypoint = SelectWaypoint(field, route);
            remaining = route.Waypoints.SkipWhile(p => p != waypoint).Skip(1).ToList();
            var submission = OperationSubmission.Create(map.Scope, "move", new { position = waypoint, tolerance = 0.15 },
                map.CollectedTick + 1800, new { position = map.Actor.Position, positionTolerance = 0.5 });
            OperationReceipt receipt = await ExecuteAsync(submission, deadline.Token);
            receipts.Add(receipt);
            if (receipt.Status != "completed") remaining.Clear();
            if (receipt.Status != "completed" && receipt.Error?.Code is not ("path_blocked" or "deadline_exceeded" or "cancelled" or "actor_dead" or "stale_scope" or "position_precondition"))
                throw new InvalidOperationException($"Navigation operation ended with {receipt.Status}: {receipt.Error?.Code}.");
            // Reobserve each segment; preserve valid remaining corners to avoid half-tile seed oscillation.
        }
        throw new NavigationPlanningException(RouteStatus.BudgetExceeded, "Navigation exhausted its 256-segment budget.");
    }

    public static IReadOnlyList<MapPosition> Subdivide(MapPosition start, IReadOnlyList<MapPosition> corners)
    {
        var result = new List<MapPosition>();
        foreach (MapPosition corner in corners)
        {
            int count = Math.Max(1, (int)Math.Ceiling(start.DistanceTo(corner) / 0.75));
            for (int index = 1; index <= count; index++)
                result.Add(new(start.X + (corner.X - start.X) * index / count, start.Y + (corner.Y - start.Y) * index / count));
            start = corner;
        }
        return result.AsReadOnly();
    }

    public async Task<ExplorationWaypoint> FindExplorationWaypointAsync(ExplorationPlanner planner, ProductionCatalog catalog,
        string wanted, MapPosition? destination = null, CancellationToken token = default)
    {
        for (int cleared = 0; ; cleared++)
        {
            SpatialSnapshot map = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            RequireAi(map);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed during exploration clearance.");
            ResourceMemorySnapshot? memory = game is IResourceMemoryReader reader ? await reader.ReadResourceMemoryAsync(map, token) : null;
            ResourceSighting? remembered = destination is null ? memory?.Nearest(wanted, catalog, map.Actor.Position) : null;
            ResourceSearchHint? hint = null;
            if (destination is null && remembered is null && memory is not null && wanted.Length > 0)
            {
                ProductionState known = await new ProductionController(game, journal).ObserveAsync(token);
                if (known.Scope != map.Scope) throw new InvalidDataException("Actor changed while reading resource search landmarks.");
                hint = memory.ProcessingAreaHint(wanted, catalog, known.Entities.Select(e => (e.Id, e.Recipe ?? e.PreviousRecipe, e.Position)), map);
                if (hint is not null)
                    await journal.AppendAsync("factory-resource-search-hint", new
                    {
                        map.Scope,
                        map.SurfaceIndex,
                        known.Tick,
                        wanted,
                        hint,
                        interpretation = "processing-area-hypothesis-not-an-observed-deposit"
                    }, token);
            }
            if (remembered is not null)
                await journal.AppendAsync("resource-memory-target", new
                {
                    map.Scope,
                    map.SurfaceIndex,
                    map.CollectedTick,
                    wanted,
                    remembered,
                    interpretation = "historical-destination-requires-local-reobservation"
                }, token);
            try { return new(planner.Choose(map, wanted, catalog, destination ?? remembered?.Position ?? hint?.Position, memory?.SurveyedCells), map.CollectedTick); }
            catch (ExplorationBlockedException) when (cleared < 16)
            {
                if (!await ClearTreeAsync(map, catalog, destination, token)) throw;
            }
        }
    }

    private async Task<bool> ClearTreeAsync(SpatialSnapshot map, ProductionCatalog catalog, MapPosition? destination, CancellationToken token)
    {
        SpatialEntity? tree = new TreeClearancePlanner().Select(map, catalog, destination);
        if (tree is null) return false;
        await journal.AppendAsync("navigation-clearance-plan", new { map.Scope, map.CollectedTick, tree, destination }, token);
        OperationReceipt receipt = await WorkAsync("mine", new { name = tree.Name, position = tree.Position, count = 1 }, 3600, token: token);
        if (receipt.Status != "completed" || receipt.Effects.GetProperty("targetId").GetString() != tree.Id
            || receipt.Effects.GetProperty("produced").GetDouble() <= 0)
            throw new InvalidOperationException("Tree clearance did not establish completed native mining; reconcile partial effects.");
        SpatialSnapshot after = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
        if (after.Scope != map.Scope || !after.Bounds.Contains(tree.Position) || after.Entities.Any(e => e.Id == tree.Id))
            throw new InvalidDataException("The mined tree's disappearance could not be verified in the current scope.");
        await journal.AppendAsync("navigation-tree-cleared", new { tree.Id, beforeTick = map.CollectedTick, afterTick = after.CollectedTick, receipt }, token);
        return true;
    }

    public static MapPosition SelectWaypoint(SpatialCollisionField field, RoutePlan route)
    {
        MapPosition start = field.Map.Actor.Position;
        int index = 0;
        while (index < route.Waypoints.Count && start.DistanceTo(route.Waypoints[index]) <= 0.15) index++;
        if (index == route.Waypoints.Count) throw new NavigationPlanningException(RouteStatus.StartBlocked, "No waypoint beyond native arrival tolerance.");
        MapPosition next = route.Waypoints[index];
        // The native move stops within 0.15 tiles. Do not repeatedly submit an already reached corner.
        // Skipping it still requires the actual body to sweep clear from the actual stopped position.
        if (index > 0 && !field.SegmentClear(start, next, 0))
            throw new NavigationPlanningException(RouteStatus.StartBlocked, "The reached corner cannot safely connect to the next segment.");
        // A clear straight line alone does not bound the native eight-direction pursuit trajectory.
        // Combine only short waypoints whose whole steering rectangle is known clear; retain tight corners otherwise.
        for (int candidate = index + 1; candidate < route.Waypoints.Count; candidate++)
        {
            var point = route.Waypoints[candidate];
            if (start.DistanceTo(point) > 8 || !field.SteeringRegionClear(start, point)) break;
            next = point;
        }
        return next;
    }

    public async Task<OperationReceipt> BuildAsync(string item, MapPosition preferredPosition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (!double.IsFinite(preferredPosition.X) || !double.IsFinite(preferredPosition.Y))
            throw new ArgumentOutOfRangeException(nameof(preferredPosition));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        while (true)
        {
            DefenseStep reflex = await DefenseStepAsync(deadline.Token);
            if (reflex.State is "defending" or "preempted" or "reconciled" or "uncertain")
            {
                await Task.Delay(150, deadline.Token);
                continue;
            }
            SpatialSnapshot map = await spatial.CaptureAsync([item], cancellationToken: deadline.Token);
            RequireAi(map);
            IReadOnlyList<PlacementCandidate> candidates = new PlacementPlanner().FindCandidates(new(map), item, preferredPosition);
            if (candidates.Count == 0) throw new InvalidOperationException("No geometric placement candidate in the current snapshot and build reach.");
            PlacementValidation validation = await spatial.ValidateAsync(map.Scope, item, candidates, deadline.Token);
            ValidatedPlacement? selected = validation.Candidates.FirstOrDefault(c => c.Allowed && c.InReach);
            if (selected is null) throw new InvalidOperationException($"No native-valid placement among the {candidates.Count} candidates tested.");
            await journal.AppendAsync("placement-plan", new { map.Scope, map.CollectedTick, item, preferredPosition, selected }, deadline.Token);
            var submission = OperationSubmission.Create(map.Scope, "build", new { item, position = selected.Position, direction = selected.Direction },
                validation.CollectedTick + 600, new
                {
                    inventory = new Dictionary<string, int> { [item] = 1 },
                    position = map.Actor.Position,
                    positionTolerance = 0.5
                });
            return await ExecuteAsync(submission, deadline.Token);
        }
    }

    private async Task<OperationReceipt> ExecuteAsync(OperationSubmission submission, CancellationToken token)
    {
        await journal.AppendAsync("submission", submission, token);
        ownedOperation = submission.OperationId;
        OperationReceipt receipt;
        try { receipt = await operations.SubmitAsync(submission, token); }
        catch (OperationOutcomeUnknownException error)
        {
            await journal.AppendAsync("outcome-unknown", new
            {
                error.OperationId,
                error.Message,
                cause = error.InnerException?.GetType().Name,
                detail = error.InnerException?.Message
            }, token);
            receipt = await QueryKnownAsync(submission.OperationId, token);
        }
        while (!receipt.IsTerminal)
        {
            await DefenseStepAsync(token);
            receipt = await QueryKnownAsync(submission.OperationId, token);
            if (!receipt.IsTerminal) await Task.Delay(100, token);
        }
        await journal.AppendAsync("receipt", receipt, token);
        ownedOperation = null;
        return receipt;
    }

    public async Task<OperationReceipt> WorkAsync(string kind, object arguments, long durationTicks,
        object? preconditions = null, CancellationToken token = default)
    {
        if (durationTicks is < 1 or > 216000) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        SpatialSnapshot initial = await spatial.CaptureAsync(cancellationToken: token);
        RequireAi(initial);
        ActorScope scope = initial.Scope;
        while (true)
        {
            DefenseStep reflex = await DefenseStepAsync(token);
            if (reflex.State is "defending" or "preempted" or "reconciled" or "uncertain")
            {
                await Task.Delay(150, token);
                continue;
            }
            SpatialSnapshot map = await spatial.CaptureAsync(cancellationToken: token);
            if (map.Scope != scope) throw new InvalidDataException("Actor scope changed before work submission; reconcile the previous intent.");
            RequireAi(map);
            return await ExecuteAsync(OperationSubmission.Create(map.Scope, kind, arguments,
                map.CollectedTick + durationTicks, preconditions), token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (ownedOperation is not null)
            {
                OperationReceipt receipt = await QueryKnownAsync(ownedOperation, stopDeadline.Token);
                if (!receipt.IsTerminal)
                {
                    await journal.AppendAsync("cancel-intent", new
                    {
                        operationId = ownedOperation,
                        reason = "Spatial controller stopping."
                    }, stopDeadline.Token);
                    try { receipt = await operations.CancelAsync(ownedOperation, stopDeadline.Token); }
                    catch (OperationOutcomeUnknownException)
                    {
                        // An ambiguous cancellation is observed, never retransmitted.
                        receipt = await QueryKnownAsync(ownedOperation, stopDeadline.Token);
                    }
                    while (!receipt.IsTerminal)
                    {
                        await Task.Delay(100, stopDeadline.Token);
                        receipt = await QueryKnownAsync(ownedOperation, stopDeadline.Token);
                    }
                }
                await journal.AppendAsync("final-receipt", receipt, stopDeadline.Token);
                if (receipt.Error?.Code == "stop_unconfirmed")
                    throw new InvalidDataException("The native spatial action has not been confirmed stopped.");
                ownedOperation = null;
            }
        }
        finally { await defense.StopOwnedActionAsync(stopDeadline.Token); }
    }

    private async Task<OperationReceipt> QueryKnownAsync(string operationId, CancellationToken token)
    {
        while (true)
        {
            try { return await operations.QueryAsync(operationId, token); }
            catch (Exception error) when (error is SocketException or TimeoutException or InvalidDataException
                || error is IOException and not SessionDivergenceException)
            {
                await Task.Delay(150, token);
            }
        }
    }

    private async Task<DefenseStep> DefenseStepAsync(CancellationToken token)
    {
        try { return await defense.StepAsync(token); }
        catch (OperationOutcomeUnknownException error)
        {
            await journal.AppendAsync("defense-outcome-unknown", new { error.OperationId, error.Message }, token);
            return new("uncertain", 0, error.OperationId);
        }
    }

    private static void RequireAi(SpatialSnapshot map)
    {
        if (map.Actor.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control. Navigation is paused.");
    }
}

public sealed record NavigationResult(MapPosition Destination, MapPosition Position, double ArrivalDistance, int Plans,
    IReadOnlyList<OperationReceipt> Receipts);
public sealed class NavigationPlanningException(RouteStatus status, string message) : Exception(message)
{
    public RouteStatus Status { get; } = status;
}
