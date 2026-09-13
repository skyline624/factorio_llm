using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StrategicReconciliationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "strategy-reconcile-" + Guid.NewGuid().ToString("N"));
    private static readonly ActorScope Scope = new("world", "old-session", "actor", 1, 3);
    private string Memory => Path.Combine(directory, "memory.json");
    private string Journal => Path.Combine(directory, "journal.jsonl");
    public StrategicReconciliationTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeathBetweenGoalsNeedsNoOldJournalButRejectsUnownedLaterWork(bool unownedOperation)
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        game.UpdatedTick = unownedOperation ? 37500 : 36100;
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(
            new StrategicMemory(1, Scope, 36100, false, "completed previous goal"), Protocol.Json));
        File.Delete(Journal);
        string original = await File.ReadAllTextAsync(Memory);
        var recovery = new RecoveryStub((_, _) => Task.CompletedTask);
        var campaign = new StrategicCampaignController(game, new NextGoal(), Memory, Journal, recovery);
        if (unownedOperation)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => campaign.RunAsync(1));
            Assert.Equal(original, await File.ReadAllTextAsync(Memory));
            Assert.Equal(0, recovery.Calls);
        }
        else
        {
            await campaign.RunAsync(1);
            Assert.Equal(1, recovery.Calls);
            Assert.False((await ReadMemoryAsync()).Pending);
            Assert.Null((await ReadMemoryAsync()).Recovery);
        }
        Assert.DoesNotContain("submit", game.Calls);
        Assert.DoesNotContain("operation", game.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CampaignRecoversBeforeAskingForAnotherGoalAfterNativeRespawn(bool segmented)
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        game.DeadObservations = 2;
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize((await ReadMemoryAsync()) with { PendingJournal = Journal }, Protocol.Json));
        var recovery = new RecoveryStub(async (death, scope) =>
        {
            var pending = await ReadMemoryAsync();
            Assert.True(pending.Pending);
            Assert.Equal(death, pending.Recovery);
            Assert.Equal(scope, pending.Scope);
        });
        var next = new NextGoal();
        string recoveryPath = Path.Combine(directory, "recovery.jsonl");
        using var segments = segmented ? new CampaignJournal(recoveryPath) : null;
        await new StrategicCampaignController(game, next, Memory, recoveryPath, recovery, segments).RunAsync(1);
        Assert.Equal(1, recovery.Calls);
        Assert.Contains("death-recovery-observed", next.Previous);
        Assert.False((await ReadMemoryAsync()).Pending);
        Assert.Null((await ReadMemoryAsync()).Recovery);
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task InterruptedSegmentReconcilesWithoutReadingOversizedCompletedHistory()
    {
        var game = await PrepareAsync(false);
        string interrupted = await File.ReadAllTextAsync(Journal);
        using var segments = new CampaignJournal(Journal);
        string pendingPath = await segments.BeginGoalAsync(0);
        await File.AppendAllTextAsync(pendingPath, interrupted);
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(
            (await ReadMemoryAsync()) with { PendingJournal = pendingPath }, Protocol.Json));
        // The old history is deliberately unreadable within the reconciliation budget.
        using (var archive = new FileStream(Journal, FileMode.Open, FileAccess.Write))
            archive.SetLength(65L * 1024 * 1024);
        var next = new NextGoal();
        await new StrategicCampaignController(game, next, Memory, Journal, campaignJournal: segments).RunAsync(1);
        Assert.Contains("interrupted-goal-reconciled", next.Previous);
        Assert.Equal(1, game.Calls.Count(c => c == "operation"));
        Assert.DoesNotContain("submit", game.Calls);
        Assert.False((await ReadMemoryAsync()).Pending);
        Assert.NotEqual(pendingPath, segments.CurrentPath);
        Assert.True(new FileInfo(pendingPath).Length < 1024 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRecoveryReconcilesBeforeRetryOrDeferralAfterAnotherDeath(bool secondDeath)
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize((await ReadMemoryAsync()) with { PendingJournal = Journal }, Protocol.Json));
        string recoveryJournal = Path.Combine(directory, "recovery.jsonl");
        int attempts = 0;
        var recovery = new RecoveryStub(async (_, scope) =>
        {
            if (++attempts != 1) return;
            var submission = OperationSubmission.Create(scope, "craft", new { recipe = "automation-science-pack", count = 1 }, 50000);
            game.OperationId = submission.OperationId;
            game.AcceptedTick = game.UpdatedTick = 40000 + game.Calls.Count;
            await new ControllerJournal(recoveryJournal).AppendAsync("submission", submission, default);
            if (secondDeath)
            {
                game.NativeIncarnation = 3;
                game.LastDeathTick = game.UpdatedTick = 40001 + game.Calls.Count;
            }
            throw new IOException("Synthetic lost recovery receipt");
        });
        var next = new NextGoal();
        await new StrategicCampaignController(game, next, Memory, recoveryJournal, recovery).RunAsync(1);
        Assert.Equal(secondDeath ? 1 : 2, recovery.Calls);
        if (secondDeath)
        {
            Assert.Contains("unsafe-corpses-deferred", next.Previous);
            using var feedback = JsonDocument.Parse(next.Previous!);
            Assert.Equal(17, feedback.RootElement.GetProperty("remaining").GetProperty("iron-plate").GetInt32());
            Assert.Equal(0, feedback.RootElement.GetProperty("collectedItemTypes").GetInt32());
        }
        Assert.Equal(2, game.Calls.Count(c => c == "operation"));
        Assert.Single(await File.ReadAllLinesAsync(recoveryJournal), line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("type").GetString() == "submission";
        });
        Assert.Null((await ReadMemoryAsync()).Recovery);
        Assert.Equal(secondDeath ? 3 : 2, (await ReadMemoryAsync()).Scope.Incarnation);
    }

    private sealed class RecoveryStub(Func<NativeDeathTransition, ActorScope, Task> action) : ICorpseRecovery
    {
        public int Calls;
        public async Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token)
        {
            Calls++;
            await action(death, scope);
            return new(40000, "collected", new Dictionary<string, long>(), new Dictionary<string, long>(), []);
        }
    }

    [Fact]
    public async Task ProvedDeathResolvesOldReceiptsAndPersistsRecoveryBeforeNewActions()
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        await new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal, afterDeath: true);
        var memory = await ReadMemoryAsync();
        Assert.False(memory.Pending);
        Assert.Equal(2, memory.Scope.Incarnation);
        Assert.NotNull(memory.Recovery);
        Assert.Equal(1, memory.Recovery.Incarnation);
        Assert.Equal(17, memory.Recovery.ActorUnitNumber);
        Assert.Contains("actor-death-reconciled", memory.PreviousResult);
        Assert.Equal(["observe", "operation", "observe"], game.Calls);
    }

    [Theory]
    [InlineData("persisted", true)]
    [InlineData("legacy", true)]
    [InlineData("different-death", false)]
    [InlineData("plain-text", false)]
    public async Task RecoveryDeathDeferralSurvivesRestartAndRequiresMatchingLegacyProof(string mode, bool deferred)
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        var death = new NativeDeathTransition(1, 37000, 17, 1, new(12, 8));
        string feedback = mode == "plain-text" ? "an old result" : JsonSerializer.Serialize(new
        {
            outcome = "actor-death-reconciled", goal = new { category = "recovery" },
            death = mode == "different-death" ? death with { DeathTick = 36900 } : death
        }, Protocol.Json);
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(new StrategicMemory(1,
            Scope with { SessionId = "new-session", Incarnation = 2, Generation = 4 }, 39999, false, feedback,
            Recovery: death, RecoveryDeathObserved: mode == "persisted"), Protocol.Json));
        var recovery = new RecoveryStub((_, _) => Task.CompletedTask);
        var result = await new StrategicRecoveryController(game, Memory, Journal, recovery).ResumeAsync(default);
        Assert.Equal(deferred ? 0 : 1, recovery.Calls);
        Assert.False(result.Pending);
        Assert.Null(result.Recovery);
        if (deferred) Assert.Contains("unsafe-corpses-deferred", result.PreviousResult);
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("running")]
    [InlineData("world")]
    public async Task DeathCannotClearAnUnknownOperationOrChangedWorld(string failure)
    {
        var game = await PrepareAsync(false);
        game.AfterDeath = true;
        game.Failure = failure;
        string original = await File.ReadAllTextAsync(Memory);
        await Assert.ThrowsAnyAsync<Exception>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal, afterDeath: true));
        Assert.Equal(original, await File.ReadAllTextAsync(Memory));
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task TerminalPartialCraftPreservesStockAndProvidesFailureFeedbackWithoutMutation()
    {
        var game = await PrepareAsync(true);
        var result = await new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal);
        var memory = await ReadMemoryAsync();
        Assert.False(memory.Pending);
        Assert.Contains("deadline_exceeded", memory.PreviousResult);
        Assert.Contains("interrupted", memory.PreviousResult);
        Assert.Equal("new-session", memory.Scope.SessionId);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
        Assert.Equal(119, report.RootElement.GetProperty("observation").GetProperty("agent").GetProperty("inventory")
            .GetProperty("automation-science-pack").GetInt32());
        Assert.Equal(["observe", "operation", "observe"], game.Calls);
    }

    [Fact]
    public async Task LostReceiptIsRecoveredByIdentityWithoutResubmission()
    {
        var game = await PrepareAsync(false);
        await new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal);
        Assert.False((await ReadMemoryAsync()).Pending);
        Assert.Single(game.Calls, c => c == "operation");
    }

    [Fact]
    public async Task LinkedPendingAttemptIsReconciledBeforeTheNextStrategicGoal()
    {
        var game = await PrepareAsync(false);
        var memory = await ReadMemoryAsync();
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(memory with { PendingJournal = Journal }, Protocol.Json));
        var runner = new NextGoal();
        await new StrategicCampaignController(game, runner, Memory, Path.Combine(directory, "next.jsonl")).RunAsync(1);
        Assert.Contains("interrupted-goal-reconciled", runner.Previous);
        Assert.False((await ReadMemoryAsync()).Pending);
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task CorruptJournalTailIsNeverDiscardedToClearPending()
    {
        var game = await PrepareAsync(true);
        await File.AppendAllTextAsync(Journal, "{\"type\":\"submission\"");
        await Assert.ThrowsAnyAsync<Exception>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal));
        Assert.True((await ReadMemoryAsync()).Pending);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task KnownFailureIsReconciledWithinTheCampaignBeforeAnotherDecision()
    {
        var game = await PrepareAsync(true);
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize((await ReadMemoryAsync()) with { Pending = false }, Protocol.Json));
        string nextJournal = Path.Combine(directory, "next.jsonl");
        var runner = new FailingThenNext(async () =>
        {
            var submission = OperationSubmission.Create(Scope with { SessionId = "new-session", Generation = 4 },
                "craft", new { recipe = "automation-science-pack", count = 120 }, 76000);
            game.OperationId = submission.OperationId;
            game.AcceptedTick = game.UpdatedTick = 40001;
            var journal = new ControllerJournal(nextJournal);
            await journal.AppendAsync("strategic-context", new { facts = "{\"observedTick\":40001}" }, default);
            await journal.AppendAsync("strategic-goal", new { category = 1, target = "science", quantity = 1, unit = 4 }, default);
            await journal.AppendAsync("submission", submission, default);
            await journal.AppendAsync("receipt", game.Receipt(), default);
        });
        var result = await new StrategicCampaignController(game, runner, Memory, nextJournal).RunAsync(2);
        Assert.Equal(2, result.GoalsExecuted);
        Assert.Contains("interrupted-goal-reconciled", runner.Next.Previous);
        Assert.Contains("execution_precondition_failed", runner.Next.Previous);
        Assert.DoesNotContain("Synthetic known partial craft failure", runner.Next.Previous);
        Assert.Contains("Synthetic known partial craft failure", await File.ReadAllTextAsync(nextJournal));
        Assert.False((await ReadMemoryAsync()).Pending);
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task LinkedAttemptRejectsAReplacementJournal()
    {
        var game = await PrepareAsync(true);
        var memory = await ReadMemoryAsync();
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(memory with { PendingJournal = Journal }, Protocol.Json));
        string other = Path.Combine(directory, "other.jsonl");
        File.Copy(Journal, other);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(other));
        Assert.True((await ReadMemoryAsync()).Pending);
        Assert.Empty(game.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("running")]
    [InlineData("stop_unconfirmed")]
    [InlineData("different-kind")]
    public async Task UnprovenOutcomeKeepsTheOriginalPendingMemory(string failure)
    {
        var game = await PrepareAsync(false);
        game.Failure = failure;
        string original = await File.ReadAllTextAsync(Memory);
        await Assert.ThrowsAnyAsync<Exception>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal));
        Assert.Equal(original, await File.ReadAllTextAsync(Memory));
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("incarnation")]
    [InlineData("moving")]
    [InlineData("queue")]
    [InlineData("scope-during-read")]
    public async Task ChangedOrActiveActorCannotClearPending(string failure)
    {
        var game = await PrepareAsync(true);
        game.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal));
        Assert.True((await ReadMemoryAsync()).Pending);
    }

    [Fact]
    public async Task ConflictingJournalAndNativeReceiptRemainBlocked()
    {
        var game = await PrepareAsync(true);
        game.Failure = "different-effects";
        await Assert.ThrowsAnyAsync<Exception>(() => new StrategicReconciliationController(game, Memory).ReconcileAsync(Journal));
        Assert.True((await ReadMemoryAsync()).Pending);
    }

    private async Task<Game> PrepareAsync(bool receipt)
    {
        var submission = OperationSubmission.Create(Scope, "craft", new { recipe = "automation-science-pack", count = 120 }, 36100);
        await File.WriteAllTextAsync(Memory, JsonSerializer.Serialize(new StrategicMemory(1, Scope, 100, true, null), Protocol.Json));
        var journal = new ControllerJournal(Journal);
        await journal.AppendAsync("strategic-context", new { observationId = "snapshot:101", facts = "{\"observedTick\":101}" }, default);
        await journal.AppendAsync("strategic-goal", new { category = 1, target = "science", quantity = 1, unit = 4 }, default);
        await journal.AppendAsync("submission", submission, default);
        var game = new Game(submission.OperationId);
        if (receipt) await journal.AppendAsync("receipt", game.Receipt(), default);
        return game;
    }

    private async Task<StrategicMemory> ReadMemoryAsync() => JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(Memory), Protocol.Json)!;
    private sealed class NextGoal : IStrategicGoalRunner
    {
        public string? Previous;
        public Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            Previous = previousResult;
            return Task.FromResult(new StrategicGoalResult(new("new", "Observe remaining science", GoalCategory.Research,
                "science", 1, GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)),
                UnsupportedReason: "Synthetic next decision without native mutation"));
        }
    }
    private sealed class FailingThenNext(Func<Task> prepare) : IStrategicGoalRunner
    {
        private bool failed;
        public NextGoal Next { get; } = new();
        public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            if (failed) return await Next.RunOnceAsync(token, previousResult);
            failed = true;
            await prepare();
            throw new InvalidOperationException("Synthetic known partial craft failure");
        }
    }
    private sealed class Game(string initialOperationId) : IGameClient
    {
        public string OperationId = initialOperationId;
        public long AcceptedTick = 110, UpdatedTick = 36100;
        public string? Failure;
        public bool AfterDeath;
        public int DeadObservations;
        public long NativeIncarnation = 2, LastDeathTick = 37000;
        public List<string> Calls { get; } = [];
        public JsonElement Receipt() => Protocol.ToElement(new
        {
            operationId = OperationId, kind = Failure == "different-kind" ? "mine" : "craft",
            status = Failure == "running" ? "running" : "failed", acceptedTick = AcceptedTick, updatedTick = UpdatedTick,
            effects = new { products = new Dictionary<string, int> { ["automation-science-pack"] = Failure == "different-effects" ? 120 : 119 } },
            error = new { code = Failure == "stop_unconfirmed" ? "stop_unconfirmed" : "deadline_exceeded", message = "Tick budget exhausted" }
        });
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            Assert.Contains(request.Action, new[] { "observe", "operation" });
            if (request.Action == "operation")
            {
                Assert.Equal(OperationId, request.Arguments.GetProperty("operationId").GetString());
                return Task.FromResult(Failure == "unknown"
                    ? new GameResponse(1, request.RequestId, false, 40000, default, new("operation_unknown", "Absent receipt"))
                    : new GameResponse(1, request.RequestId, true, 40000, Receipt()));
            }
            bool alive = !AfterDeath || DeadObservations-- <= 0;
            var current = Scope with { SessionId = "new-session", Generation = AfterDeath ? 4 + NativeIncarnation - 2 : 4,
                Incarnation = AfterDeath && alive ? NativeIncarnation : 1 };
            if (Failure == "world") current = current with { WorldId = "other" };
            if (Failure == "incarnation") current = current with { Incarnation = 2 };
            if (Failure == "scope-during-read" && Calls.Count > 1) current = current with { Generation = 5 };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 40000 + Calls.Count, Protocol.ToElement(new
            {
                scope = current, collectedTick = 40000 + Calls.Count,
                recovery = new { knownCorpsesComplete = true,
                    corpses = new[] { new { id = $"corpse:{NativeIncarnation + 15}:{LastDeathTick}:1", incarnation = NativeIncarnation - 1,
                        deathTick = LastDeathTick, actorUnitNumber = NativeIncarnation + 15, surfaceIndex = 1, position = new MapPosition(12, 8),
                        inventories = new { corpse = new { items = new Dictionary<string, long> { ["iron-plate"] = 17 } } } } },
                    lastDeath = new
                    { incarnation = NativeIncarnation - 1, tick = LastDeathTick, unitNumber = NativeIncarnation + 15,
                        surfaceIndex = 1, position = new MapPosition(12, 8) } },
                agent = new { alive, controlMode = "ai", stopUnconfirmed = false,
                    walking = Failure == "moving", mining = false, shooting = false, craftingQueueSize = Failure == "queue" ? 1 : 0,
                    inventory = new Dictionary<string, int> { ["automation-science-pack"] = 119 } },
                operation = Receipt(), goal = new { rocketsLaunched = 0 }
            })));
        }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
