namespace Factorio.Agent.Core;

/// <summary>A planning view assembled from the actor's native photographs. Unsurveyed tiles remain solid obstacles.</summary>
public sealed class SurveyedTransportFrame
{
    public const int MaximumTiles = 262144;
    internal const string UnknownTile = "__unsurveyed_transport_tile__";
    private readonly Dictionary<(int X, int Y), string> tiles = [];
    private readonly Dictionary<string, SpatialEntity> entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGeometry> prototypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollisionMask> terrain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlaceableItem> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fluids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StationaryThreat> threats = new(StringComparer.Ordinal);
    private SpatialSnapshot? latest;
    private WorldBox? bounds;
    public int Samples { get; private set; }
    public long FirstTick { get; private set; }

    public void Add(SpatialSnapshot sample)
    {
        if (!sample.Coverage.Atomic || !sample.Coverage.Complete || sample.Coverage.Visibility != "current-character-local-area"
            || sample.Coverage.Radius is < 4 or > SpatialSnapshot.MaximumRadius
            || sample.Bounds.Width is < 1 or > 129 || sample.Bounds.Height is < 1 or > 129 || sample.TilePrototypes.ContainsKey(UnknownTile))
            throw new InvalidDataException("A corridor requires bounded complete native photographs.");
        if (latest is not null && (sample.Scope != latest.Scope || sample.SurfaceIndex != latest.SurfaceIndex
            || sample.CollectedTick < latest.CollectedTick || sample.Actor.Id != latest.Actor.Id))
            throw new InvalidDataException("The surveyed corridor changed actor, surface or time.");
        _ = new SpatialCollisionField(sample); // Reject missing/overlapping terrain before retaining any data.
        var next = bounds is null ? sample.Bounds : new WorldBox(
            new(Math.Min(bounds.Min.X, sample.Bounds.Min.X), Math.Min(bounds.Min.Y, sample.Bounds.Min.Y)),
            new(Math.Max(bounds.Max.X, sample.Bounds.Max.X), Math.Max(bounds.Max.Y, sample.Bounds.Max.Y)));
        if (next.Width * next.Height > MaximumTiles) throw new InvalidOperationException("Transport survey exceeds its tile budget.");
        foreach (var id in entities.Where(p => p.Key == sample.Actor.Id || sample.Bounds.Contains(p.Value.Position)).Select(p => p.Key).ToArray())
            entities.Remove(id);
        foreach (var entity in sample.Entities) entities[entity.Id] = entity;
        foreach (var row in sample.Rows)
            for (int x = row.X; x < row.X + row.Length; x++) tiles[(x, row.Y)] = row.Name;
        foreach (var entry in sample.Prototypes) prototypes[entry.Key] = entry.Value;
        foreach (var entry in sample.TilePrototypes) terrain[entry.Key] = entry.Value;
        foreach (var entry in sample.Items) items[entry.Key] = entry.Value;
        foreach (var entry in sample.TileFluids ?? new Dictionary<string, string>()) fluids[entry.Key] = entry.Value;
        // These are dated sightings, not a new sensor or a claim of current visibility. Merely walking away
        // must not forget an attack envelope which could make the proposed construction impossible.
        foreach (var threat in sample.StationaryThreats ?? []) threats[threat.Id] = threat;
        if (threats.Count > 1000) throw new InvalidOperationException("Transport survey exceeds its stationary sighting budget.");
        if (latest is null) FirstTick = sample.CollectedTick;
        latest = sample; bounds = next; Samples++;
    }

    public SpatialSnapshot Build()
    {
        if (latest is null || bounds is null) throw new InvalidOperationException("No native corridor observations exist.");
        var rows = new List<TileRun>();
        for (int y = (int)bounds.Min.Y; y < bounds.Max.Y; y++)
        {
            int start = (int)bounds.Min.X;
            string name = tiles.GetValueOrDefault((start, y), UnknownTile);
            for (int x = start + 1; x < bounds.Max.X; x++)
            {
                string next = tiles.GetValueOrDefault((x, y), UnknownTile);
                if (next == name) continue;
                rows.Add(new(start, y, x - start, name)); start = x; name = next;
            }
            rows.Add(new(start, y, (int)bounds.Max.X - start, name));
        }
        var masks = new Dictionary<string, CollisionMask>(terrain, StringComparer.Ordinal)
        {
            [UnknownTile] = new(prototypes.Values.SelectMany(p => p.Mask.Layers).Distinct(StringComparer.Ordinal).ToArray(), false, false, false)
        };
        return latest with
        {
            Bounds = bounds, Rows = rows, Entities = entities.Values.ToArray(), Prototypes = new Dictionary<string, EntityGeometry>(prototypes),
            TilePrototypes = masks, Items = new Dictionary<string, PlaceableItem>(items), TileFluids = new Dictionary<string, string>(fluids),
            StationaryThreats = threats.Values.ToArray(),
            Coverage = new(false, false, "historical-character-survey-with-blocked-unknown-tiles", latest.Coverage.Radius)
        };
    }
}
