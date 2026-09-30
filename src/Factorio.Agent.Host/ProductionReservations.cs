namespace Factorio.Agent.Host;

/// <summary>
/// Propagates protected native stocks through nested production calls within the single actor owner. Reserved entities are
/// neither reused nor emptied; a collectable one stays reserved against reuse but its finished stock may be taken.
/// </summary>
internal static class ProductionReservations
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> Active = new();
    private static readonly AsyncLocal<IReadOnlySet<string>?> Sources = new();
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    public static IReadOnlySet<string> Current => Active.Value ?? Empty;
    public static IReadOnlySet<string> Collectable => Sources.Value ?? Empty;

    /// <summary>Whether collection may take this entity's output.</summary>
    public static bool Collects(string entityId) => !Current.Contains(entityId) || Collectable.Contains(entityId);

    public static IDisposable Enter(IReadOnlySet<string>? entityIds, IReadOnlySet<string>? collectable = null)
    {
        var previous = (Active.Value, Sources.Value);
        Active.Value = Current.Concat(entityIds ?? Empty).ToHashSet(StringComparer.Ordinal);
        Sources.Value = Collectable.Concat(collectable ?? Empty).ToHashSet(StringComparer.Ordinal);
        return new Scope(previous);
    }

    /// <summary>
    /// Every registered cell entity: a cell assembler would fight its inserters and input chests feed their machines. Output
    /// chests of ready cells hold finished stock that logistics would carry anyway, so production may collect from them.
    /// Callers enter this themselves because an AsyncLocal value set inside an async method does not flow back.
    /// </summary>
    public static IDisposable EnterFactory(FactoryState state) => Enter(
        state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal),
        FactoryLogistics.OutputChests(state.Cells).ToHashSet(StringComparer.Ordinal));

    private sealed class Scope((IReadOnlySet<string>? Active, IReadOnlySet<string>? Sources) previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            (Active.Value, Sources.Value) = previous;
            disposed = true;
        }
    }
}
