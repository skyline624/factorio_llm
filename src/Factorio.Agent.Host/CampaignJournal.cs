namespace Factorio.Agent.Host;

/// <summary>Keeps the active strategic attempt separate from completed campaign history.</summary>
public sealed class CampaignJournal(string indexPath) : IControllerJournal, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string IndexPath { get; } = Path.GetFullPath(indexPath);
    public string CurrentPath { get; private set; } = Path.GetFullPath(indexPath);

    public async Task<string> BeginGoalAsync(int goalNumber, CancellationToken token = default)
    {
        if (goalNumber is < 0 or >= 10000) throw new ArgumentOutOfRangeException(nameof(goalNumber));
        await gate.WaitAsync(token);
        try
        {
            string segment = Path.Combine(Path.GetDirectoryName(IndexPath)!,
                $"{Path.GetFileNameWithoutExtension(IndexPath)}-goal-{Guid.NewGuid():N}.jsonl");
            // Never reuse or truncate a prior attempt, including after a process restart.
            await using (var created = new FileStream(segment, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await created.FlushAsync(token);
                created.Flush(flushToDisk: true);
            }
            await new ControllerJournal(segment).AppendAsync("goal-segment",
                new { indexPath = IndexPath, goalIndex = goalNumber }, token);
            await new ControllerJournal(IndexPath).AppendAsync("goal-journal",
                new { goalIndex = goalNumber, journalPath = segment }, token);
            // The caller persists this path as PendingJournal before dispatching the goal.
            CurrentPath = segment;
            return segment;
        }
        finally { gate.Release(); }
    }

    public async Task AppendAsync(string type, object data, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await new ControllerJournal(CurrentPath).AppendAsync(type, data, token); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
