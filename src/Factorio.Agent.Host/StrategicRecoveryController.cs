using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Durable bridge from a failed goal through native respawn, receipt reconciliation and corpse collection.</summary>
public sealed class StrategicRecoveryController(IGameClient game, string memoryPath, string journalPath, ICorpseRecovery? recovery = null)
{
    public async Task<StrategicMemory> ResumeAsync(CancellationToken token)
    {
        var journal = new ControllerJournal(journalPath);
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var memory = await ReadMemoryAsync(token);
            var observed = await ObserveAsync(token);
            var scope = observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            if (!observed.Data.GetProperty("agent").GetProperty("alive").GetBoolean() || scope.Incarnation != memory.Scope.Incarnation)
            {
                await WaitForRespawnAsync(memory, observed, token);
                await new StrategicReconciliationController(game, memoryPath)
                    .ReconcileAsync(memory.PendingJournal ?? journalPath, token, afterDeath: true);
                memory = await ReadMemoryAsync(token);
            }
            else if (memory.Pending)
            {
                if (memory.PendingJournal is null) throw new InvalidDataException("The interrupted attempt has no linked journal for automatic reconciliation.");
                await new StrategicReconciliationController(game, memoryPath).ReconcileAsync(memory.PendingJournal, token);
                memory = await ReadMemoryAsync(token);
            }
            if (memory.Recovery is not { } death) return memory;
            observed = await ObserveAsync(token);
            scope = RequireIdle(observed, memory.Scope, memory.Tick);
            if (scope.Incarnation != death.Incarnation + 1) throw new InvalidDataException("The recovery marker belongs to another incarnation.");
            memory = memory with { Scope = scope, Tick = observed.Tick, Pending = true, PendingJournal = Path.GetFullPath(journalPath) };
            await LocalJson.WriteAsync(memoryPath, memory, token);
            await journal.AppendAsync("strategic-context", new
            {
                observationId = $"recovery:{observed.Tick}",
                facts = JsonSerializer.Serialize(new { observedTick = observed.Tick, death }, Protocol.Json)
            }, token);
            await journal.AppendAsync("strategic-goal", new { category = "recovery", target = "proven-own-corpses", quantity = 1, unit = "completion" }, token);
            try
            {
                var result = await (recovery ?? new CorpseRecoveryController(game, journal)).RunAsync(death, scope, token);
                var after = await ObserveAsync(token);
                if (RequireIdle(after, scope, memory.Tick) != scope || after.Tick < result.Tick)
                    throw new InvalidDataException("Actor changed before committing the recovery outcome.");
                string feedback = JsonSerializer.Serialize(new
                {
                    outcome = "death-recovery-observed", observedTick = after.Tick, death,
                    recoveryOutcome = result.Outcome, collectedThisAttempt = result.Collected.Take(24).ToDictionary(p => p.Key, p => p.Value),
                    remaining = result.Remaining.Take(24).ToDictionary(p => p.Key, p => p.Value),
                    collectedItemTypes = result.Collected.Count, remainingItemTypes = result.Remaining.Count,
                    interpretation = "Only native transfers from existing corpses were made. Deferred or missing items are not recovered stock. Re-observe the factory and resume or rebuild toward the rocket; never replay old actions."
                }, Protocol.Json);
                if (feedback.Length > 4000) throw new InvalidDataException("Recovery feedback exceeds the strategic context budget.");
                memory = new(1, scope, after.Tick, false, feedback);
                await LocalJson.WriteAsync(memoryPath, memory, token);
                return memory;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                lastFailure = error;
                await journal.AppendAsync("strategic-execution-error", new
                {
                    failureCode = "recovery_failed", exceptionType = error.GetType().FullName,
                    message = error.Message[..Math.Min(error.Message.Length, 2000)]
                }, token);
                // The next iteration must first prove every previous receipt terminal, including a second death.
            }
        }
        throw new InvalidOperationException("Recovery exhausted four reconciled attempts; its pending journal and native world remain intact.", lastFailure);
    }

    private async Task WaitForRespawnAsync(StrategicMemory memory, GameResponse observed, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        NativeDeathTransition? proof = null;
        while (true)
        {
            var current = NativeDeathTransition.Read(observed, memory.Scope, memory.Tick, awaitingRespawn: true);
            if (proof is not null && current != proof) throw new InvalidDataException("The native death changed while waiting for respawn.");
            proof = current;
            if (observed.Data.GetProperty("agent").GetProperty("alive").GetBoolean()) return;
            await Task.Delay(150, deadline.Token);
            observed = await ObserveAsync(deadline.Token);
        }
    }

    private async Task<StrategicMemory> ReadMemoryAsync(CancellationToken token)
    {
        var memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)
            ?? throw new InvalidDataException("Missing strategic recovery memory.");
        if (memory.Version != 1 || memory.Scope is null || memory.Tick < 0 || memory.PreviousResult?.Length > 4000)
            throw new InvalidDataException("Malformed strategic recovery memory.");
        return memory;
    }

    private async Task<GameResponse> ObserveAsync(CancellationToken token)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        return response;
    }

    private static ActorScope RequireIdle(GameResponse response, ActorScope previous, long earliestTick)
    {
        var data = response.Data;
        var scope = data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var actor = data.GetProperty("agent");
        if (response.Tick < earliestTick || data.GetProperty("collectedTick").GetInt64() != response.Tick
            || scope.WorldId != previous.WorldId || scope.ActorId != previous.ActorId || scope.Incarnation != previous.Incarnation
            || scope.Generation < previous.Generation || !actor.GetProperty("alive").GetBoolean()
            || actor.GetProperty("controlMode").GetString() != "ai" || actor.GetProperty("stopUnconfirmed").GetBoolean()
            || actor.GetProperty("walking").GetBoolean() || actor.GetProperty("mining").GetBoolean()
            || actor.GetProperty("shooting").GetBoolean() || actor.GetProperty("craftingQueueSize").GetInt32() != 0)
            throw new InvalidDataException("Recovery requires a confirmed idle actor in the same world and incarnation.");
        if (data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object)
        {
            if (operation.GetProperty("status").GetString() is not ("completed" or "partial" or "failed" or "cancelled" or "rejected")
                || operation.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.GetProperty("code").GetString() == "stop_unconfirmed")
                throw new InvalidDataException("Recovery has an active, unknown or unconfirmed native operation.");
        }
        return scope;
    }
}
