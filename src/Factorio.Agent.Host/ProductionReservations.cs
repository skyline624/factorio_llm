namespace Factorio.Agent.Host;

/// <summary>Propagates protected native stocks through nested production calls within the single actor owner.</summary>
internal static class ProductionReservations
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> Active = new();
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    public static IReadOnlySet<string> Current => Active.Value ?? Empty;

    public static IDisposable Enter(IReadOnlySet<string>? entityIds)
    {
        var previous = Active.Value;
        Active.Value = Current.Concat(entityIds ?? Empty).ToHashSet(StringComparer.Ordinal);
        return new Scope(previous);
    }

    private sealed class Scope(IReadOnlySet<string>? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            Active.Value = previous;
            disposed = true;
        }
    }
}
