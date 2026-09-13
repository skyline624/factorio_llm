using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CampaignJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-campaign-journal-" + Guid.NewGuid().ToString("N"));
    private string Memory => Path.Combine(directory, "memory.json");
    private string Index => Path.Combine(directory, "campaign.jsonl");
    public CampaignJournalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task EveryGoalHasItsOwnDurablePendingJournalAndPreservesEarlierEvidence()
    {
        using var journal = new CampaignJournal(Index);
        var runner = new Runner(journal, Memory);
        var result = await new StrategicCampaignController(new Game(), runner, Memory, Index, campaignJournal: journal).RunAsync(2);
        Assert.Equal(2, result.GoalsExecuted);
        Assert.Equal(2, runner.PendingPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(Index, runner.PendingPaths);
        for (int index = 0; index < 2; index++)
        {
            string contents = await File.ReadAllTextAsync(runner.PendingPaths[index]);
            Assert.Contains($"\"goalNumber\":{index + 1}", contents);
            Assert.DoesNotContain($"\"goalNumber\":{2 - index}", contents);
        }
        var references = (await File.ReadAllLinesAsync(Index)).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(runner.PendingPaths, references.Where(r => r.RootElement.GetProperty("type").GetString() == "goal-journal")
                .Select(r => r.RootElement.GetProperty("data").GetProperty("journalPath").GetString()!).ToArray());
        }
        finally { foreach (var reference in references) reference.Dispose(); }
    }

    [Fact]
    public async Task CancellationKeepsOnlyTheInterruptedSegmentLinkedForReconciliation()
    {
        using var cancellation = new CancellationTokenSource();
        using var journal = new CampaignJournal(Index);
        var runner = new Runner(journal, Memory, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StrategicCampaignController(new Game(), runner, Memory, Index, campaignJournal: journal).RunAsync(2, cancellation.Token));
        var pending = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(Memory), Protocol.Json)!;
        Assert.True(pending.Pending);
        Assert.Equal(runner.PendingPaths[1], pending.PendingJournal);
        Assert.NotEqual(runner.PendingPaths[0], pending.PendingJournal);
        Assert.DoesNotContain("\"goalNumber\":2", await File.ReadAllTextAsync(runner.PendingPaths[0]));
    }

    [Fact]
    public async Task IndexWriteFailurePreventsGoalDispatchAndPendingMarker()
    {
        Directory.CreateDirectory(Index);
        using var journal = new CampaignJournal(Index);
        var runner = new Runner(journal, Memory);
        var error = await Record.ExceptionAsync(() =>
            new StrategicCampaignController(new Game(), runner, Memory, Index, campaignJournal: journal).RunAsync(1));
        Assert.NotNull(error);
        Assert.Empty(runner.PendingPaths);
        Assert.False(File.Exists(Memory));
    }

    private sealed class Runner(CampaignJournal journal, string memoryPath, CancellationTokenSource? cancel = null) : IStrategicGoalRunner
    {
        public List<string> PendingPaths { get; } = [];
        public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            var memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
            PendingPaths.Add(memory.PendingJournal!);
            await journal.AppendAsync("goal-evidence", new { goalNumber = PendingPaths.Count }, token);
            if (PendingPaths.Count == 2 && cancel is not null)
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return new(new("o", "Test research", GoalCategory.Research, "automation", 1, GoalUnit.Completion,
                GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)), Research: new("automation", true, 1, 2, ["automation"]));
        }
    }

    private sealed class Game : IGameClient
    {
        private long tick = 100;
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GameResponse(1, request.RequestId, true, ++tick, Protocol.ToElement(new
            {
                scope = new ActorScope("world", "session", "actor", 1, 1),
                agent = new { alive = true, controlMode = "ai" }, goal = new { rocketsLaunched = 0 },
                operation = new { status = "completed" }
            })));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
