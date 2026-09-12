namespace Factorio.Agent.Core;

public sealed record ResourceSighting(string EntityId, string Name, MapPosition Position, long ObservedTick);
public sealed record ResourceSearchHint(string EntityId, string Recipe, MapPosition Position);
public sealed record SurveyedCell(int X, int Y);
public interface IResourceMemoryReader
{
    Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default);
}

/// <summary>Historical destinations and surveyed coverage, never present inventory or collision data.</summary>
public sealed record ResourceMemorySnapshot(string WorldId, int SurfaceIndex, long LastTick,
    IReadOnlyList<ResourceSighting> Resources, IReadOnlyList<SurveyedCell> SurveyedCells, bool Truncated = false, int Version = 1)
{
    private const int ResourceLimit = 8192, SurveyLimit = 100000;
    public static ResourceMemorySnapshot Empty(SpatialSnapshot map) => new(map.Scope.WorldId, map.SurfaceIndex, map.CollectedTick, [], []);

    public void ValidateFor(SpatialSnapshot map)
    {
        if (Version != 1 || WorldId != map.Scope.WorldId || SurfaceIndex != map.SurfaceIndex || LastTick > map.CollectedTick || LastTick < 0
            || Resources is null || SurveyedCells is null || Resources.Count > ResourceLimit || SurveyedCells.Count > SurveyLimit
            || Resources.Any(r => string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.EntityId)
                || r.ObservedTick < 0 || r.ObservedTick > LastTick || !double.IsFinite(r.Position.X) || !double.IsFinite(r.Position.Y)))
            throw new InvalidDataException("Resource history has incompatible identity, time or contents.");
    }

    public ResourceMemorySnapshot Merge(SpatialSnapshot map)
    {
        ValidateFor(map);
        if (!map.Coverage.Atomic || !map.Coverage.Complete || map.Coverage.Visibility != "current-character-local-area")
            throw new InvalidDataException("Only complete native local observations can update resource history.");
        var current = map.Entities.Where(e => map.Prototypes.TryGetValue(e.Name, out var p) && p.Type is "resource" or "tree"
                && e.Amount is null or > 0)
            .Select(e => new ResourceSighting(e.Id, e.Name, e.Position, map.CollectedTick));
        // Within current coverage, absence invalidates a former sample. A single actual entity
        // per name/8-tile cell bounds memory without inventing a patch centroid or its stock.
        var samples = Resources.Where(r => !map.Bounds.Contains(r.Position)).Concat(current)
            .GroupBy(r => (r.Name, X: checked((int)Math.Floor(r.Position.X / 8)), Y: checked((int)Math.Floor(r.Position.Y / 8))))
            .Select(g => g.OrderByDescending(r => r.ObservedTick).ThenBy(r => r.EntityId, StringComparer.Ordinal).First())
            .OrderByDescending(r => r.ObservedTick).ThenBy(r => r.EntityId, StringComparer.Ordinal).ToArray();
        var cells = SurveyedCells.ToHashSet();
        for (int x = checked((int)Math.Ceiling(map.Bounds.Min.X / 4)); x < map.Bounds.Max.X / 4; x++)
            for (int y = checked((int)Math.Ceiling(map.Bounds.Min.Y / 4)); y < map.Bounds.Max.Y / 4; y++) cells.Add(new(x, y));
        return new(WorldId, SurfaceIndex, map.CollectedTick, samples.Take(ResourceLimit).ToArray(),
            cells.OrderBy(c => new MapPosition(c.X * 4d, c.Y * 4d).DistanceTo(map.Actor.Position))
                .ThenBy(c => c.Y).ThenBy(c => c.X).Take(SurveyLimit).ToArray(),
            Truncated || samples.Length > ResourceLimit || cells.Count > SurveyLimit);
    }

    public ResourceSearchHint? ProcessingAreaHint(string item, ProductionCatalog catalog,
        IEnumerable<(string Id, string? Recipe, MapPosition Position)> known, SpatialSnapshot current)
    {
        ValidateFor(current);
        if (catalog.Scope != current.Scope) throw new InvalidDataException("Resource search catalog belongs to another actor scope.");
        var surveyed = SurveyedCells.ToHashSet();
        return known.Where(e => e.Recipe is not null && !current.Bounds.Contains(e.Position)
                && !surveyed.Contains(new(checked((int)Math.Floor(e.Position.X / 4)), checked((int)Math.Floor(e.Position.Y / 4))))
                && catalog.Recipes.Any(r => r.Name == e.Recipe && r.Ingredients.Any(i => i.Name == item && i.DeterministicItem)))
            .OrderBy(e => e.Position.DistanceTo(current.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new ResourceSearchHint(e.Id, e.Recipe!, e.Position)).FirstOrDefault();
    }

    public ResourceSighting? Nearest(string item, ProductionCatalog catalog, MapPosition from) => Resources
        .Where(r => catalog.Mining.TryGetValue(r.Name, out var products) && products.Any(p => p.Name == item && p.DeterministicItem))
        .OrderBy(r => r.Position.DistanceTo(from)).ThenByDescending(r => r.ObservedTick).FirstOrDefault();
}
