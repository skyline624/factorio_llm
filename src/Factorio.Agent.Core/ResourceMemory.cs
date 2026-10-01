namespace Factorio.Agent.Core;

/// <summary>An actual resource entity remembered as a destination. Its origin tells a local view from a reading of the map.</summary>
public sealed record ResourceSighting(string EntityId, string Name, MapPosition Position, long ObservedTick, string Origin = ResourceSighting.Local)
{
    /// <summary>Seen in a complete native local view of the actor.</summary>
    public const string Local = "local";
    /// <summary>Read in a chunk of the force's map: a destination hint the actor has not observed itself.</summary>
    public const string Charted = "charted";
}
public sealed record ResourceSearchHint(string EntityId, string Recipe, MapPosition Position);
public sealed record SurveyedCell(int X, int Y);
public interface IResourceMemoryReader
{
    Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default);
}

/// <summary>Historical destinations and surveyed coverage, never present inventory or collision data.</summary>
public sealed record ResourceMemorySnapshot(string WorldId, int SurfaceIndex, long LastTick,
    IReadOnlyList<ResourceSighting> Resources, IReadOnlyList<SurveyedCell> SurveyedCells, bool Truncated = false,
    int Version = ResourceMemorySnapshot.CurrentVersion)
{
    /// <summary>Version 2 records each sighting's origin; version 1 files hold local sightings only and are still read.</summary>
    public const int CurrentVersion = 2;
    private const int ResourceLimit = 8192, SurveyLimit = 100000;
    public static ResourceMemorySnapshot Empty(SpatialSnapshot map) => Empty(map.Scope.WorldId, map.SurfaceIndex, map.CollectedTick);
    public static ResourceMemorySnapshot Empty(string worldId, int surfaceIndex, long tick) => new(worldId, surfaceIndex, tick, [], []);

    public void ValidateFor(SpatialSnapshot map) => ValidateFor(map.Scope.WorldId, map.SurfaceIndex, map.CollectedTick);

    public void ValidateFor(string worldId, int surfaceIndex, long tick)
    {
        if (Version is not (1 or CurrentVersion) || WorldId != worldId || SurfaceIndex != surfaceIndex || LastTick > tick || LastTick < 0
            || Resources is null || SurveyedCells is null || Resources.Count > ResourceLimit || SurveyedCells.Count > SurveyLimit
            || Resources.Any(r => string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.EntityId)
                || r.ObservedTick < 0 || r.ObservedTick > LastTick || !double.IsFinite(r.Position.X) || !double.IsFinite(r.Position.Y)
                || r.Origin is not (ResourceSighting.Local or ResourceSighting.Charted)))
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
        // Within current coverage, absence invalidates a former sample, charted or not.
        var samples = Compact(Resources.Where(r => !map.Bounds.Contains(r.Position)).Concat(current));
        var cells = SurveyedCells.ToHashSet();
        for (int x = checked((int)Math.Ceiling(map.Bounds.Min.X / 4)); x < map.Bounds.Max.X / 4; x++)
            for (int y = checked((int)Math.Ceiling(map.Bounds.Min.Y / 4)); y < map.Bounds.Max.Y / 4; y++) cells.Add(new(x, y));
        return new(WorldId, SurfaceIndex, map.CollectedTick, samples.Take(ResourceLimit).ToArray(),
            cells.OrderBy(c => new MapPosition(c.X * 4d, c.Y * 4d).DistanceTo(map.Actor.Position))
                .ThenBy(c => c.Y).ThenBy(c => c.X).Take(SurveyLimit).ToArray(),
            Truncated || samples.Length > ResourceLimit || cells.Count > SurveyLimit);
    }

    /// <summary>
    /// Adds the deposits of a map reading as charted destinations. Inside the reading's coverage they replace the earlier charted
    /// samples of the same names. Local sightings stay until a local view replaces them, and no surveyed cell is added: a map
    /// reading never stands for local terrain.
    /// </summary>
    public ResourceMemorySnapshot MergeCharted(ChartedResourceSnapshot charted)
    {
        ValidateFor(charted.Scope.WorldId, charted.SurfaceIndex, charted.CollectedTick);
        var names = charted.Names.ToHashSet(StringComparer.Ordinal);
        var read = charted.Deposits.Select(d => new ResourceSighting(d.Sample.Id, d.Name, d.Sample.Position, charted.CollectedTick,
            ResourceSighting.Charted));
        var samples = Compact(Resources.Where(r => r.Origin != ResourceSighting.Charted || !names.Contains(r.Name) || !charted.Covers(r.Position))
            .Concat(read));
        return this with { LastTick = charted.CollectedTick, Resources = samples.Take(ResourceLimit).ToArray(),
            Truncated = Truncated || samples.Length > ResourceLimit, Version = CurrentVersion };
    }

    // A single actual entity per name/8-tile cell bounds memory without inventing a patch centroid or its stock.
    // The newest wins; at the same tick a local view outranks a map reading.
    private static ResourceSighting[] Compact(IEnumerable<ResourceSighting> sightings) => sightings
        .GroupBy(r => (r.Name, X: checked((int)Math.Floor(r.Position.X / 8)), Y: checked((int)Math.Floor(r.Position.Y / 8))))
        .Select(g => g.OrderByDescending(r => r.ObservedTick).ThenBy(r => r.Origin == ResourceSighting.Charted)
            .ThenBy(r => r.EntityId, StringComparer.Ordinal).First())
        .OrderByDescending(r => r.ObservedTick).ThenBy(r => r.EntityId, StringComparer.Ordinal).ToArray();

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

    public ResourceSighting? Nearest(string item, ProductionCatalog catalog, MapPosition from, Func<MapPosition, bool>? allowed = null) => Resources
        .Where(r => catalog.Mining.TryGetValue(r.Name, out var products) && products.Any(p => p.Name == item && p.DeterministicItem)
            && (allowed is null || allowed(r.Position)))
        .OrderBy(r => r.Position.DistanceTo(from)).ThenByDescending(r => r.ObservedTick).FirstOrDefault();

    /// <summary>The remembered deposit of one resource nearest to a point, locally observed or charted alike, among allowed ones.</summary>
    public ResourceSighting? NearestOf(string resource, MapPosition from, Func<ResourceSighting, bool>? allowed = null) => Resources
        .Where(r => r.Name == resource && (allowed is null || allowed(r)))
        .OrderBy(r => r.Position.DistanceTo(from)).ThenByDescending(r => r.ObservedTick).ThenBy(r => r.EntityId, StringComparer.Ordinal)
        .FirstOrDefault();
}
