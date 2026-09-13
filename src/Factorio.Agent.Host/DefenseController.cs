using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using System.Net.Sockets;

namespace Factorio.Agent.Host;

/// <summary>Single sequential actor arbiter. No inference call is part of this control loop.</summary>
public sealed class DefenseController(IGameClient game, IControllerJournal journal)
{
    private readonly OperationClient operations = new(game);
    private string? uncertainOperation;
    private string? ownedOperation;
    private OperationSubmission? ownedSubmission;
    private long lastTick = -1;

    public async Task<DefenseStep> StepAsync(CancellationToken token = default)
    {
        if (uncertainOperation is not null)
        {
            // An ambiguous submission/cancellation is reconciled by identity, never retransmitted.
            OperationReceipt reconciled = await operations.QueryAsync(uncertainOperation, token);
            await journal.AppendAsync("reconciled", reconciled, token);
            if (ownedSubmission?.OperationId == reconciled.OperationId) EquipmentReceipt.Validate(ownedSubmission, reconciled);
            uncertainOperation = null;
            return new("reconciled", reconciled.UpdatedTick, reconciled.OperationId);
        }
        SafetyObservation observation = SafetyObservation.Parse(
            await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200 }), token));
        if (observation.Tick < lastTick) throw new SessionDivergenceException("The safety observation tick regressed.");
        lastTick = observation.Tick;
        if (observation.Operation is { IsTerminal: true } finished && finished.OperationId == ownedOperation)
        {
            await journal.AppendAsync("receipt", finished, token);
            if (ownedSubmission is not null) EquipmentReceipt.Validate(ownedSubmission, finished);
            ownedOperation = null;
            ownedSubmission = null;
        }
        VisibleThreat? target = DefensePolicy.SelectTarget(observation);
        EquipmentDecision? equipment = target is null ? EquipmentPolicy.Select(observation) : null;
        if (target is null && equipment is null) return new("observing", observation.Tick);
        if (observation.Operation is { IsTerminal: false } active)
        {
            if (active.OperationId == ownedOperation) return new("defending", observation.Tick, active.OperationId);
            if (target is null && observation.Enemies.Count == 0) return new("observing", observation.Tick);
            await journal.AppendAsync("cancel-intent", new { active.OperationId, observation.Tick, targetId = target?.Id,
                reason = target is not null ? "Visible enemy in current weapon range preempts existing work."
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
        var submission = OperationSubmission.Create(observation.Scope, equipment?.Kind ?? "shoot",
            equipment?.Arguments ?? new { entityId = target!.Id, ticks = 60 },
            observation.Tick + 180, new { position = observation.Position, positionTolerance = 0.5 });
        await journal.AppendAsync("submission", submission, token);
        ownedOperation = submission.OperationId;
        ownedSubmission = submission;
        try
        {
            OperationReceipt receipt = await operations.SubmitAsync(submission, token);
            await journal.AppendAsync("receipt", receipt, token);
            EquipmentReceipt.Validate(submission, receipt);
            if (receipt.IsTerminal) { ownedOperation = null; ownedSubmission = null; }
            return new(receipt.IsTerminal && equipment is null ? "resolved" : "defending", receipt.UpdatedTick, receipt.OperationId);
        }
        catch (OperationOutcomeUnknownException)
        {
            uncertainOperation = submission.OperationId;
            throw;
        }
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
        if (!receipt.IsTerminal)
        {
            await journal.AppendAsync("cancel-intent", new { operationId = ownedOperation, reason = "Controller stopping." }, token);
            receipt = await operations.CancelAsync(ownedOperation, token);
        }
        await journal.AppendAsync("final-receipt", receipt, token);
        if (receipt.IsTerminal) ownedOperation = null;
    }
}

public sealed record DefenseStep(string State, long Tick, string? OperationId = null);
