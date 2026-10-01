using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Bounded process memory of defense reflex fights. Attack detection reads it as evidence that a cluster was attacked even
/// when the pack is gone; it never commands anything and is lost with the process, as a restart reobserves the world.
/// </summary>
public sealed class ReflexEventLog
{
    public const int Capacity = 256;
    public static ReflexEventLog Shared { get; } = new();
    private readonly Queue<ReflexEvent> events = new();
    private readonly Lock gate = new();

    public void Record(ReflexEvent fight)
    {
        lock (gate)
        {
            events.Enqueue(fight);
            while (events.Count > Capacity) events.Dequeue();
        }
    }

    public IReadOnlyList<ReflexEvent> Since(long tick)
    {
        lock (gate) return events.Where(e => e.Tick > tick).ToArray();
    }
}
