using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

public interface IStrategicGoalRunner
{
    Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null);
}

public sealed record StrategicCampaignResult(bool RocketLaunched, int GoalsExecuted, long EndTick, string StopReason = "goal-budget");
public sealed record StrategicMemory(int Version, ActorScope Scope, long Tick, bool Pending, string? PreviousResult,
    string? PendingJournal = null, NativeDeathTransition? Recovery = null, bool RecoveryDeathObserved = false);

/// <summary>Sequential strategic goals under the caller's actor lease. Unknown outcomes are never retried.</summary>
public sealed class StrategicCampaignController(IGameClient game, IStrategicGoalRunner runner, string memoryPath,
    string? journalPath = null, ICorpseRecovery? recovery = null, CampaignJournal? campaignJournal = null,
    Func<CancellationToken, Task>? maintenance = null)
{
    public const int MaxConsecutiveFailures = 5;

    public async Task<StrategicCampaignResult> RunAsync(int maxGoals, CancellationToken token = default)
    {
        if (maxGoals is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxGoals));
        if (campaignJournal is not null && journalPath is not null && Path.GetFullPath(journalPath) != campaignJournal.IndexPath)
            throw new ArgumentException("The campaign journal index does not match the configured journal path.", nameof(journalPath));
        string? activeJournalPath = campaignJournal?.CurrentPath ?? journalPath;
        var observation = await ObserveAsync(token, allowDead: true);
        StrategicMemory memory = File.Exists(memoryPath)
            ? JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)
                ?? throw new InvalidDataException("Empty strategic memory.")
            : new(1, observation.Scope, observation.Tick, false, null);
        if (memory.Version != 1 || memory.Scope is null || memory.Tick < 0 || memory.PreviousResult?.Length > 4000)
            throw new InvalidDataException("Malformed strategic memory.");
        if ((!observation.Alive || memory.Scope.Incarnation != observation.Scope.Incarnation || memory.Recovery is not null)
            && activeJournalPath is not null)
        {
            memory = await new StrategicRecoveryController(game, memoryPath, activeJournalPath, recovery).ResumeAsync(token);
            observation = await ObserveAsync(token);
        }
        Validate(memory, observation);
        if (memory.Pending && memory.PendingJournal is { } pendingJournal)
        {
            memory = await new StrategicRecoveryController(game, memoryPath, activeJournalPath ?? pendingJournal, recovery).ResumeAsync(token);
            observation = await ObserveAsync(token);
        }
        if (memory.Pending) throw new InvalidDataException("An earlier strategic execution has no verified terminal outcome. Reconcile its journal and native effects before resuming.");
        string? lastGoal = null;
        int repeated = 0, consecutiveFailures = 0;
        for (int index = 0; index < maxGoals; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index > 0 && maintenance is not null)
            {
                // Persistent cells keep producing only if restocked between decisions. A maintenance failure is the
                // maintainer's own journal entry; an operation it left active still blocks the next goal below.
                try { await maintenance(token); }
                catch (Exception) when (!token.IsCancellationRequested) { }
                observation = await ObserveAsync(token, allowDead: true);
                // A death between goals, such as an attack during logistics, follows the same recovery as one during a goal.
                // On 2026-10-01 (seed 20261002) the campaign stopped instead because the actor died during maintenance.
                if ((!observation.Alive || observation.Scope.Incarnation != memory.Scope.Incarnation) && activeJournalPath is not null)
                {
                    memory = await new StrategicRecoveryController(game, memoryPath, activeJournalPath, recovery).ResumeAsync(token);
                    observation = await ObserveAsync(token);
                }
            }
            Validate(memory, observation);
            if (observation.Rockets > 0) return new(true, index, observation.Tick, "rocket-observed");
            if (campaignJournal is not null) activeJournalPath = await campaignJournal.BeginGoalAsync(index, token);
            // Persist uncertainty before any goal can dispatch native actions. Exceptions leave this marker intact.
            await LocalJson.WriteAsync(memoryPath, memory with { Scope = observation.Scope, Tick = observation.Tick, Pending = true,
                PendingJournal = activeJournalPath is null ? null : Path.GetFullPath(activeJournalPath) }, token);
            StrategicGoalResult result;
            CampaignObservation after;
            try
            {
                result = await runner.RunOnceAsync(token, memory.PreviousResult);
                after = await ObserveAsync(token);
                Validate(memory with { Tick = observation.Tick }, after);
                if (after.Scope != observation.Scope) throw new InvalidDataException("Actor scope changed during strategic execution; reconcile partial effects.");
            }
            catch (Exception error) when (!token.IsCancellationRequested && activeJournalPath is not null)
            {
                string failureCode = error switch
                {
                    GameRpcException rpc => rpc.Error.Code,
                    NavigationPlanningException => "navigation_blocked",
                    PlannerException => "planner_unavailable",
                    TimeoutException => "controller_budget_exhausted",
                    InvalidDataException => "observation_inconsistent",
                    InvalidOperationException => "execution_precondition_failed",
                    IOException => "transport_or_storage_failed",
                    _ => "execution_failed"
                };
                // Detailed diagnostics stay in the private journal; only the bounded failure category goes to the model.
                await new ControllerJournal(activeJournalPath).AppendAsync("strategic-execution-error", new
                {
                    exceptionType = error.GetType().FullName, failureCode,
                    message = error.Message[..Math.Min(error.Message.Length, 2000)],
                    stackTrace = error.StackTrace is { } stack ? stack[..Math.Min(stack.Length, 4000)] : null
                }, token);
                memory = await new StrategicRecoveryController(game, memoryPath, activeJournalPath, recovery).ResumeAsync(token);
                observation = await ObserveAsync(token);
                // A failed goal ends reconciled and idle; stop before the model can loop on the same cause.
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                    return new(false, index + 1, observation.Tick, "repeated-failure");
                memory = memory with { PreviousResult = WithFailureStreak(memory.PreviousResult, consecutiveFailures) };
                await LocalJson.WriteAsync(memoryPath, memory, token);
                continue;
            }
            consecutiveFailures = 0;
            string feedback = Feedback(result, after.Tick, compact: false);
            if (feedback.Length > 4000) feedback = Feedback(result, after.Tick, compact: true);
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

    // The compact form keeps counts and the first identifiers so a large verified result can never block the campaign.
    private static string Feedback(StrategicGoalResult result, long tick, bool compact)
    {
        var defense = result.Defense is { } d && compact
            ? d with { ReadyIds = d.ReadyIds.Take(8).ToArray(), ExposedIds = d.ExposedIds.Take(8).ToArray() } : result.Defense;
        string feedback = JsonSerializer.Serialize(new
        {
            observedTick = tick,
            goal = new { result.Goal.Category, result.Goal.Target, result.Goal.Quantity, result.Goal.Unit },
            result.UnsupportedReason,
            result.Production,
            result.Fluid,
            result.Rocket,
            Defense = defense,
            automation = result.Automation?.Stages.Select(s => new { s.Recipe, s.Machines, s.CraftsPerMinute }).ToArray(),
            result.Logistics,
            research = result.Research is { } research ? new { research.Target, research.Researched, research.StartTick,
                research.EndTick, completedCount = research.CompletedTechnologies.Count,
                recentCompleted = research.CompletedTechnologies.TakeLast(16).ToArray() } : null,
            evidenceTruncated = compact ? true : (bool?)null,
            evidenceScope = "Historical verified result; current stock and conditions must be observed again."
        }, Protocol.Json);
        if (compact && feedback.Length > 4000)
            feedback = JsonSerializer.Serialize(new
            {
                observedTick = tick,
                goal = new { result.Goal.Category, result.Goal.Target, result.Goal.Quantity, result.Goal.Unit },
                completed = result.UnsupportedReason is null, evidenceTruncated = true,
                evidenceScope = "Verified details exceeded the context budget; observe current stock and conditions again."
            }, Protocol.Json);
        return feedback;
    }

    private static string? WithFailureStreak(string? previous, int failures)
    {
        const string guidance = "Consecutive strategic goals failed. Do not repeat the failed goal unchanged; choose a goal that removes the reported cause or makes different progress.";
        if (previous is null) return null;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(previous) is not System.Text.Json.Nodes.JsonObject node) return previous;
            node["consecutiveFailures"] = failures;
            node["guidance"] = guidance;
            string annotated = node.ToJsonString(Protocol.Json);
            return annotated.Length <= 4000 ? annotated : previous;
        }
        catch (JsonException) { return previous; }
    }

    private async Task<CampaignObservation> ObserveAsync(CancellationToken token, bool allowDead = false)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var data = response.Data;
        var agent = data.GetProperty("agent");
        if ((!allowDead && !agent.GetProperty("alive").GetBoolean()) || agent.GetProperty("controlMode").GetString() != "ai")
            throw new InvalidDataException("The strategic actor is unavailable or under manual control; reconcile before continuing.");
        if (data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object
            && operation.TryGetProperty("status", out var status)
            && status.GetString() is not ("completed" or "partial" or "failed" or "cancelled" or "rejected"))
            throw new InvalidDataException("A native operation is still active or unknown; reconcile before starting another strategic goal.");
        return new(data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)
            ?? throw new InvalidDataException("Missing strategic actor scope."), response.Tick,
            data.GetProperty("goal").GetProperty("rocketsLaunched").GetInt32(), agent.GetProperty("alive").GetBoolean());
    }

    private static void Validate(StrategicMemory memory, CampaignObservation observation)
    {
        if (!observation.Alive || memory.Version != 1 || memory.Scope is null || memory.Scope.WorldId != observation.Scope.WorldId
            || memory.Scope.ActorId != observation.Scope.ActorId || memory.Scope.Incarnation != observation.Scope.Incarnation
            || memory.Tick < 0 || observation.Tick < memory.Tick || observation.Rockets < 0
            || memory.PreviousResult?.Length > 4000)
            throw new InvalidDataException("Strategic memory belongs to another world, actor, incarnation or future state, or is malformed.");
    }
    private sealed record CampaignObservation(ActorScope Scope, long Tick, int Rockets, bool Alive);
}
