using System.Diagnostics;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Wall-clock cost of game calls by action, split between the engine round trip and local bookkeeping, appended to
/// <c>call-timings.jsonl</c> about once a minute. On 2026-10-06 (seed 20261106) 58 % of game time passed between native
/// operations; this measures where, without changing any call.
/// </summary>
internal sealed class CallTimings(string directory)
{
    public const string FileName = "call-timings.jsonl";
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private long windowStart = Stopwatch.GetTimestamp();

    private sealed class Entry
    {
        public int Calls;
        public double TotalMs, EngineMs, LocalMs, MaxMs;
    }

    public void Record(string action, TimeSpan total, TimeSpan engine)
    {
        string? line = null;
        lock (gate)
        {
            if (!entries.TryGetValue(action, out var entry)) entries[action] = entry = new Entry();
            entry.Calls++;
            entry.TotalMs += total.TotalMilliseconds;
            entry.EngineMs += engine.TotalMilliseconds;
            entry.LocalMs += (total - engine).TotalMilliseconds;
            entry.MaxMs = Math.Max(entry.MaxMs, total.TotalMilliseconds);
            var elapsed = Stopwatch.GetElapsedTime(windowStart);
            if (elapsed >= Period)
            {
                line = JsonSerializer.Serialize(new
                {
                    recordedUtc = DateTime.UtcNow,
                    windowSeconds = Math.Round(elapsed.TotalSeconds, 1),
                    actions = entries.OrderByDescending(p => p.Value.TotalMs).ToDictionary(p => p.Key, p => new
                    {
                        calls = p.Value.Calls,
                        totalMs = Math.Round(p.Value.TotalMs),
                        engineMs = Math.Round(p.Value.EngineMs),
                        localMs = Math.Round(p.Value.LocalMs),
                        maxMs = Math.Round(p.Value.MaxMs)
                    })
                }, Protocol.Json);
                entries.Clear();
                windowStart = Stopwatch.GetTimestamp();
            }
        }
        // Diagnostics only: a failed append never affects the call it measured.
        if (line is not null)
            try { File.AppendAllText(Path.Combine(directory, FileName), line + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}
