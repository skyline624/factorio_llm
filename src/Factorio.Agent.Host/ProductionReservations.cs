namespace Factorio.Agent.Host;

/// <summary>
/// Propagates protected native stocks through nested production calls within the single actor owner. Reserved entities are
/// neither repurposed nor emptied; finished stock and a ready miner's existing storage connection remain usable.
/// </summary>
internal static class ProductionReservations
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> Active = new();
    private static readonly AsyncLocal<IReadOnlySet<string>?> Sources = new();
    private static readonly AsyncLocal<IReadOnlyList<ExtractionPair>?> Miners = new();
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    public static IReadOnlySet<string> Current => Active.Value ?? Empty;
    public static IReadOnlySet<string> Collectable => Sources.Value ?? Empty;
    internal sealed record ExtractionPair(string Item, string DrillId, string ChestId);
    internal static IReadOnlyList<ExtractionPair> Extractors => Miners.Value ?? [];

    /// <summary>Whether collection may take this entity's output.</summary>
    public static bool Collects(string entityId) => !Current.Contains(entityId) || Collectable.Contains(entityId);

    public static IDisposable Enter(IReadOnlySet<string>? entityIds, IReadOnlySet<string>? collectable = null)
        => Enter(entityIds, collectable, []);

    private static IDisposable Enter(IReadOnlySet<string>? entityIds, IReadOnlySet<string>? collectable,
        IReadOnlyList<ExtractionPair> extractors)
    {
        var previous = (Active.Value, Sources.Value, Miners.Value);
        Active.Value = Current.Concat(entityIds ?? Empty).ToHashSet(StringComparer.Ordinal);
        Sources.Value = Collectable.Concat(collectable ?? Empty).ToHashSet(StringComparer.Ordinal);
        // An explicit inner reservation or a fresher factory state can revoke an inherited ready connection.
        Miners.Value = Extractors.Where(p => entityIds?.Contains(p.DrillId) != true && entityIds?.Contains(p.ChestId) != true)
            .Concat(extractors).Distinct().ToArray();
        return new Scope(previous);
    }

    /// <summary>
    /// Every registered cell entity: a cell assembler would fight its inserters and input chests feed their machines. Output
    /// chests of every cell, ready or not, hold finished stock that logistics would carry anyway, so production may collect from them.
    /// Callers enter this themselves because an AsyncLocal value set inside an async method does not flow back.
    /// </summary>
    public static IDisposable EnterFactory(FactoryState state) => Enter(
        state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal),
        FactoryLogistics.OutputChests(state.Cells).ToHashSet(StringComparer.Ordinal),
        state.Cells.Where(c => c.Kind == "miner" && c.Status == "ready" && c.Recipe is not null
            && c.Entities.ContainsKey("drill") && c.Entities.ContainsKey("output-chest"))
            .Select(c => new ExtractionPair(c.Recipe!, c.Entities["drill"], c.Entities["output-chest"])).ToArray());

    private sealed class Scope((IReadOnlySet<string>? Active, IReadOnlySet<string>? Sources, IReadOnlyList<ExtractionPair>? Miners) previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            (Active.Value, Sources.Value, Miners.Value) = previous;
            disposed = true;
        }
    }
}
