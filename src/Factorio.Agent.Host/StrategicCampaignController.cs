using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public interface IStrategicGoalRunner
{
    Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null);
}

public sealed record StrategicCampaignResult(bool RocketLaunched, int GoalsExecuted, long EndTick, string StopReason = "goal-budget");
public sealed record StrategicMemory(int Version, ActorScope Scope, long Tick, bool Pending, string? PreviousResult, string? PendingJournal = null);

/// <summary>Sequential strategic goals under the caller's actor lease. Unknown outcomes are never retried.</summary>
public sealed class StrategicCampaignController(IGameClient game, IStrategicGoalRunner runner, string memoryPath, string? journalPath = null)
{
    public async Task<StrategicCampaignResult> RunAsync(int maxGoals, CancellationToken token = default)
    {
        if (maxGoals is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxGoals));
        var observation = await ObserveAsync(token);
        StrategicMemory memory = File.Exists(memoryPath)
            ? JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)
                ?? throw new InvalidDataException("Empty strategic memory.")
            : new(1, observation.Scope, observation.Tick, false, null);
        Validate(memory, observation);
        if (memory.Pending && memory.PendingJournal is { } pendingJournal)
        {
            await new StrategicReconciliationController(game, memoryPath).ReconcileAsync(pendingJournal, token);
            memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
            observation = await ObserveAsync(token);
        }
        if (memory.Pending) throw new InvalidDataException("An earlier strategic execution has no verified terminal outcome. Reconcile its journal and native effects before resuming.");
        string? lastGoal = null;
        int repeated = 0;
        for (int index = 0; index < maxGoals; index++)
        {
            token.ThrowIfCancellationRequested();
            Validate(memory, observation);
            if (observation.Rockets > 0) return new(true, index, observation.Tick, "rocket-observed");
            // Persist uncertainty before any goal can dispatch native actions. Exceptions leave this marker intact.
            await LocalJson.WriteAsync(memoryPath, memory with { Scope = observation.Scope, Tick = observation.Tick, Pending = true,
                PendingJournal = journalPath is null ? null : Path.GetFullPath(journalPath) }, token);
            StrategicGoalResult result;
            try { result = await runner.RunOnceAsync(token, memory.PreviousResult); }
            catch (Exception) when (!token.IsCancellationRequested && journalPath is not null)
            {
                await new StrategicReconciliationController(game, memoryPath).ReconcileAsync(journalPath, token);
                memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
                observation = await ObserveAsync(token);
                continue;
            }
            var after = await ObserveAsync(token);
            Validate(memory with { Tick = observation.Tick }, after);
            if (after.Scope != observation.Scope) throw new InvalidDataException("Actor scope changed during strategic execution; reconcile partial effects.");
            string feedback = JsonSerializer.Serialize(new
            {
                observedTick = after.Tick,
                goal = new { result.Goal.Category, result.Goal.Target, result.Goal.Quantity, result.Goal.Unit },
                result.UnsupportedReason,
                result.Production,
                result.Fluid,
                result.Rocket,
                research = result.Research is { } research ? new { research.Target, research.Researched, research.StartTick,
                    research.EndTick, completedCount = research.CompletedTechnologies.Count,
                    recentCompleted = research.CompletedTechnologies.TakeLast(16).ToArray() } : null,
                evidenceScope = "Historical verified result; current stock and conditions must be observed again."
            }, Protocol.Json);
            if (feedback.Length > 4000) throw new InvalidDataException("Strategic feedback exceeds the bounded context.");
            memory = new(1, after.Scope, after.Tick, false, feedback);
            await LocalJson.WriteAsync(memoryPath, memory, token);
            observation = after;
            string goalKey = JsonSerializer.Serialize(new { result.Goal.Category, result.Goal.Target, result.Goal.Quantity, result.Goal.Unit }, Protocol.Json);
            repeated = lastGoal == goalKey ? repeated + 1 : 1;
            lastGoal = goalKey;
            if (observation.Rockets > 0) return new(true, index + 1, observation.Tick, "rocket-observed");
            if (repeated >= 3) return new(false, index + 1, observation.Tick, "repeated-goal");
        }
        return new(observation.Rockets > 0, maxGoals, observation.Tick);
    }

    private async Task<CampaignObservation> ObserveAsync(CancellationToken token)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var data = response.Data;
        var agent = data.GetProperty("agent");
        if (!agent.GetProperty("alive").GetBoolean() || agent.GetProperty("controlMode").GetString() != "ai")
            throw new InvalidDataException("The strategic actor is unavailable or under manual control; reconcile before continuing.");
        if (data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object
            && operation.TryGetProperty("status", out var status)
            && status.GetString() is not ("completed" or "partial" or "failed" or "cancelled" or "rejected"))
            throw new InvalidDataException("A native operation is still active or unknown; reconcile before starting another strategic goal.");
        return new(data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)
            ?? throw new InvalidDataException("Missing strategic actor scope."), response.Tick,
            data.GetProperty("goal").GetProperty("rocketsLaunched").GetInt32());
    }

    private static void Validate(StrategicMemory memory, CampaignObservation observation)
    {
        if (memory.Version != 1 || memory.Scope is null || memory.Scope.WorldId != observation.Scope.WorldId
            || memory.Scope.ActorId != observation.Scope.ActorId || memory.Scope.Incarnation != observation.Scope.Incarnation
            || memory.Tick < 0 || observation.Tick < memory.Tick || observation.Rockets < 0
            || memory.PreviousResult?.Length > 4000)
            throw new InvalidDataException("Strategic memory belongs to another world, actor, incarnation or future state, or is malformed.");
    }
    private sealed record CampaignObservation(ActorScope Scope, long Tick, int Rockets);
}
