using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using System.Net.Sockets;

namespace Factorio.Agent.Host;

/// <summary>Single sequential actor arbiter. No inference call is part of this control loop.</summary>
public sealed class DefenseController(IGameClient game, IControllerJournal journal, ReflexEventLog? reflexes = null,
    FactoryRegistry? registry = null)
{
    private readonly OperationClient operations = new(game);
    private readonly ReflexEventLog fights = reflexes ?? ReflexEventLog.Shared;
    private readonly PortableDefenseDeployment portable = new(game, journal,
        registry ?? (game is SessionGameClient session ? new FactoryRegistry(session.Directory) : null));
    private string? uncertainOperation;
    private string? ownedOperation;
    private OperationSubmission? ownedSubmission;
    private bool ownedStopRequested;
    private long lastTick = -1;
    private (ActorScope Scope, MapPosition Position)? navigationDestination;

    /// <summary>Paid defense must keep this same actor's active navigation endpoint free.</summary>
    internal IDisposable ProtectNavigationDestination(ActorScope scope, MapPosition position)
    {
        var previous = navigationDestination;
        navigationDestination = (scope, position);
        return new NavigationProtection(this, previous);
    }

    private sealed class NavigationProtection(DefenseController owner,
        (ActorScope Scope, MapPosition Position)? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            owner.navigationDestination = previous;
            disposed = true;
        }
    }

    public async Task<DefenseStep> StepAsync(CancellationToken token = default, GameResponse? observed = null)
    {
        if (uncertainOperation is not null)
        {
            // An ambiguous submission/cancellation is reconciled by identity, never retransmitted.
            OperationReceipt reconciled = await operations.QueryAsync(uncertainOperation, token);
            await journal.AppendAsync("reconciled", reconciled, token);
            if (ownedSubmission?.OperationId == reconciled.OperationId)
            {
                await AcceptAsync(ownedSubmission, reconciled, token);
                if (reconciled.IsTerminal) { ownedOperation = null; ownedSubmission = null; ownedStopRequested = false; }
            }
            uncertainOperation = null;
            return new("reconciled", reconciled.UpdatedTick, reconciled.OperationId);
        }
        SafetyObservation observation = SafetyObservation.Parse(
            observed ?? await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200 }), token));
        if (observation.Tick < lastTick) throw new SessionDivergenceException("The safety observation tick regressed.");
        lastTick = observation.Tick;
        if (observation.Operation is { IsTerminal: true } finished && finished.OperationId == ownedOperation)
        {
            await journal.AppendAsync("receipt", finished, token);
            if (ownedSubmission is not null) await AcceptAsync(ownedSubmission, finished, token);
            ownedOperation = null;
            ownedSubmission = null;
            ownedStopRequested = false;
        }
        VisibleThreat? target = DefensePolicy.SelectTarget(observation);
        EquipmentDecision? equipment = target is null ? EquipmentPolicy.Select(observation) : null;
        OperationSubmission? portableAction = observation.Operation is { IsTerminal: false } running
            && running.OperationId == ownedOperation && running.Kind != "shoot" ? null : await portable.NextAsync(observation, token,
                navigationDestination is { } destination && destination.Scope == observation.Scope ? destination.Position : null);
        if (portable.ObservationChanged) return new("defending", observation.Tick);
        bool retreat = RetreatPlanner.Needed(observation);
        (SpatialSnapshot Map, RetreatPlan Plan)? cover = null;
        if (portableAction is null && !retreat && (RetreatPlanner.SeeksCover(observation) || RetreatPlanner.RepositionsInCover(observation)))
        {
            // Planned before preempting: an unreachable refuge never interrupts a fight the actor can still win.
            cover = await PlanRetreatAsync(observation, token);
            if (cover.Value.Plan.Status == "observation-changed") return new("defending", cover.Value.Map.CollectedTick);
            retreat = cover.Value.Plan.Next is not null;
        }
        if (observation.Operation is { IsTerminal: false } own && own.OperationId == ownedOperation
            && !((retreat || portableAction is not null) && own.Kind == "shoot"))
            return new("defending", observation.Tick, own.OperationId);
        if (target is null && equipment is null && !retreat && portableAction is null) return new("observing", observation.Tick);
        if (observation.Operation is { IsTerminal: false } active)
        {
            if (active.OperationId == ownedOperation && !((retreat || portableAction is not null) && active.Kind == "shoot"))
                return new("defending", observation.Tick, active.OperationId);
            if (portableAction is null && target is null && observation.Enemies.Count == 0) return new("observing", observation.Tick);
            await journal.AppendAsync("cancel-intent", new { active.OperationId, observation.Tick, targetId = target?.Id,
                reason = portableAction is not null ? "A visible pack requires paid local turret protection."
                    : retreat ? "Visible danger requires retreat on observed terrain."
                    : target is not null ? "Visible enemy in current weapon range preempts existing work."
                    : "A visible enemy requires restoring carried weapons before continuing work." }, token);
            try
            {
                OperationReceipt stopped = await operations.CancelAsync(active.OperationId, token);
                await journal.AppendAsync("cancel-receipt", stopped, token);
                if (!stopped.IsTerminal || stopped.Error?.Code == "stop_unconfirmed")
                    throw new InvalidDataException("Preemption did not establish a stopped native action.");
                // Observe afresh after cancellation, including scope, pilot, visibility and inventory.
                return new("preempted", stopped.UpdatedTick, active.OperationId);
            }
            catch (OperationOutcomeUnknownException)
            {
                uncertainOperation = active.OperationId;
                throw;
            }
        }
        OperationSubmission? submission = portableAction;
        if (submission is null && retreat)
        {
            var (map, plan) = cover ?? await PlanRetreatAsync(observation, token);
            if (plan.Status == "observation-changed") return new("defending", map.CollectedTick);
            if (plan.Next is not null)
                // Native shooting slows walking: preserve full retreat speed against mobile or untyped threats.
                submission = OperationSubmission.Create(map.Scope, "move", target is null || !observation.SupportsMovingFire
                    || !observation.LocalEnemiesComplete || observation.Enemies.Any(e => e.Type is not ("turret" or "unit-spawner"))
                    ? new { position = plan.Next, tolerance = RetreatPlanner.MoveTolerance }
                    : (object)new { position = plan.Next, tolerance = RetreatPlanner.MoveTolerance, shootEntityId = target.Id },
                    map.CollectedTick + 180, new { position = map.Actor.Position, positionTolerance = .5 });
        }
        if (submission is null && target is null && equipment is null) return new("observing", observation.Tick);
        submission ??= OperationSubmission.Create(observation.Scope, equipment?.Kind ?? "shoot",
            equipment?.Arguments ?? new { entityId = target!.Id, ticks = 60 }, observation.Tick + 180,
            new { position = observation.Position, positionTolerance = 0.5 });
        await journal.AppendAsync("submission", submission, token);
        ownedOperation = submission.OperationId;
        ownedSubmission = submission;
        ownedStopRequested = false;
        // A fight is attack evidence for the industry around it, even once the pack is gone.
        if (submission.Kind is "shoot" or "move" && observation.Position is { } actor && observation.Enemies.Count > 0)
            fights.Record(new(observation.Tick, submission.Kind == "shoot" ? "shoot" : "retreat", actor,
                (target ?? observation.Enemies.MinBy(e => actor.DistanceTo(e.Position)))!.Position, observation.Enemies.Count));
        try
        {
            OperationReceipt receipt = await operations.SubmitAsync(submission, token);
            await journal.AppendAsync("receipt", receipt, token);
            await AcceptAsync(submission, receipt, token);
            if (receipt.IsTerminal) { ownedOperation = null; ownedSubmission = null; }
            return new(receipt.IsTerminal && equipment is null && portableAction is null ? "resolved" : "defending", receipt.UpdatedTick, receipt.OperationId);
        }
        catch (OperationOutcomeUnknownException)
        {
            uncertainOperation = submission.OperationId;
            throw;
        }
    }

    private async Task AcceptAsync(OperationSubmission submission, OperationReceipt receipt, CancellationToken token)
    {
        EquipmentReceipt.Validate(submission, receipt);
        MovementFireReceipt.Validate(submission, receipt);
        await portable.AcceptAsync(submission, receipt, token);
    }

    private async Task<(SpatialSnapshot Map, RetreatPlan Plan)> PlanRetreatAsync(SafetyObservation observation, CancellationToken token)
    {
        var map = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
        var plan = new RetreatPlanner().Find(observation, map, token);
        await journal.AppendAsync("retreat-plan", new { map.Scope, map.CollectedTick, observation.Health, plan,
            reason = RetreatPlanner.Needed(observation) ? "danger"
                : RetreatPlanner.RepositionsInCover(observation) ? "covered-separation" : "outnumbered-cover",
            interpretation = "Observed paths toward loaded-turret coverage or increased enemy separation; no guarantee against unseen or faster threats." }, token);
        return (map, plan);
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken token = default)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        await journal.AppendAsync("controller-start", new { durationSeconds = duration.TotalSeconds,
            policy = "visible-enemy-within-equipped-bullet-weapon-range", llmDependency = false }, token);
        try
        {
            while (true)
            {
                try
                {
                    await StepAsync(deadline.Token);
                }
                catch (Exception error) when (error is OperationOutcomeUnknownException or TimeoutException or SocketException
                    || error is IOException and not SessionDivergenceException)
                {
                    await journal.AppendAsync("transport-unavailable", new { error = error.GetType().Name,
                        error.Message, uncertainOperation }, deadline.Token);
                }
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopOwnedActionAsync(stopDeadline.Token);
        }
    }

    public async Task StopOwnedActionAsync(CancellationToken token)
    {
        if (ownedOperation is null) return;
        OperationReceipt receipt = await operations.QueryAsync(ownedOperation, token);
        if (!receipt.IsTerminal && !ownedStopRequested)
        {
            await journal.AppendAsync("cancel-intent", new { operationId = ownedOperation, reason = "Controller stopping." }, token);
            ownedStopRequested = true;
            try { receipt = await operations.CancelAsync(ownedOperation, token); }
            catch (OperationOutcomeUnknownException) { receipt = await operations.QueryAsync(ownedOperation, token); }
        }
        while (!receipt.IsTerminal)
        {
            await Task.Delay(100, token);
            receipt = await operations.QueryAsync(ownedOperation, token);
        }
        await journal.AppendAsync("final-receipt", receipt, token);
        if (receipt.Error?.Code == "stop_unconfirmed") throw new InvalidDataException("The native defense action has not been confirmed stopped.");
        if (ownedSubmission is not null) await AcceptAsync(ownedSubmission, receipt, token);
        if (uncertainOperation == ownedOperation) uncertainOperation = null;
        ownedOperation = null;
        ownedSubmission = null;
        ownedStopRequested = false;
    }
}

public sealed record DefenseStep(string State, long Tick, string? OperationId = null);
