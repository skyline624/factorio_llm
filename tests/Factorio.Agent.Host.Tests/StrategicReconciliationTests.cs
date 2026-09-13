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
            var current = Scope with { SessionId = "new-session", Generation = 4 };
            if (Failure == "world") current = current with { WorldId = "other" };
            if (Failure == "incarnation") current = current with { Incarnation = 2 };
            if (Failure == "scope-during-read" && Calls.Count > 1) current = current with { Generation = 5 };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 40000 + Calls.Count, Protocol.ToElement(new
            {
                scope = current, agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false,
                    walking = Failure == "moving", mining = false, shooting = false, craftingQueueSize = Failure == "queue" ? 1 : 0,
                    inventory = new Dictionary<string, int> { ["automation-science-pack"] = 119 } },
                operation = Receipt(), goal = new { rocketsLaunched = 0 }
            })));
        }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
