using System.Net.Sockets;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Executes C# spatial plans while yielding actor operations to the deterministic defense loop.</summary>
public sealed class SpatialController(IGameClient game, IControllerJournal journal) : IAsyncDisposable
{
    private readonly SpatialClient spatial = new(game);
    private readonly OperationClient operations = new(game);
    private readonly DefenseController defense = new(game, journal);
    private string? ownedOperation;

    public async Task<NavigationResult> NavigateAsync(MapPosition destination, double arrivalDistance = 0.4,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(arrivalDistance) || arrivalDistance is < 0.2 or > 10)
            throw new ArgumentOutOfRangeException(nameof(arrivalDistance));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var receipts = new List<OperationReceipt>();
        var remaining = new List<MapPosition>();
        int plans = 0;
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
                await Task.Delay(500, deadline.Token);
                continue;
            }
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
            await journal.AppendAsync("route-plan", new { map.Scope, map.CollectedTick, destination, arrivalDistance, reused = reusable, route }, deadline.Token);
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
                validation.CollectedTick + 600, new { inventory = new Dictionary<string, int> { [item] = 1 },
                    position = map.Actor.Position, positionTolerance = 0.5 });
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
            await journal.AppendAsync("outcome-unknown", new { error.OperationId, error.Message }, token);
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
        while (true)
        {
            DefenseStep reflex = await DefenseStepAsync(token);
            if (reflex.State is "defending" or "preempted" or "reconciled" or "uncertain")
            {
                await Task.Delay(150, token);
                continue;
            }
            SpatialSnapshot map = await spatial.CaptureAsync(cancellationToken: token);
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
                    await journal.AppendAsync("cancel-intent", new { operationId = ownedOperation,
                        reason = "Spatial controller stopping." }, stopDeadline.Token);
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
