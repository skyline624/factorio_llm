namespace Factorio.Agent.Core;

public sealed record ExtractionPlacement(PlacementCandidate Drill, string ReceiverId, MapPosition OutputPosition,
    IReadOnlyList<string> ResourceIds);
public sealed record ExtractionSite(PlacementCandidate Receiver, ExtractionPlacement Connection);
public sealed record InstalledExtraction(string DrillId, ExtractionPlacement Connection);

/// <summary>Solves native drill output containment, tile alignment and resource coverage against observed receivers.</summary>
public sealed class ExtractionPlanner
{
    public static bool SupportsSolidOutput(EntityGeometry drill) => drill.Type == "mining-drill" && drill.MiningOutput is not null
        && (drill.IsElectric || drill.FuelCategories is { Count: > 0 });

    public static bool HasRemainingResources(SpatialSnapshot map, SpatialEntity installed)
    {
        EntityGeometry drill = map.Prototypes[installed.Name];
        if (drill.Type != "mining-drill" || drill.MiningRadius is not > 0 || !double.IsFinite(drill.MiningRadius.Value)
            || drill.ResourceCategories is null) throw new InvalidDataException("Missing native drill coverage.");
        double radius = drill.MiningRadius.Value;
        var area = new WorldBox(new(installed.Position.X - radius, installed.Position.Y - radius),
            new(installed.Position.X + radius, installed.Position.Y + radius));
        if (!map.Coverage.Complete || !map.Coverage.Atomic || !map.Bounds.Contains(area))
            throw new InvalidDataException("The complete mining area must be observed before proving depletion.");
        return EligibleDeposits(map, drill).Any(e => area.Overlaps(e.Bounds));
    }

    private static IEnumerable<SpatialEntity> EligibleDeposits(SpatialSnapshot map, EntityGeometry drill) =>
        map.Entities.Where(e => e.Amount is > 0 && map.Prototypes[e.Name].ResourceCategory is { } category
            && drill.ResourceCategories!.ContainsKey(category));

    public static double WorkEnergy(ExtractionPlacement connection, SpatialSnapshot map, string drillItem,
        ProductionCatalog catalog, string resourceItem, double quantity)
    {
        if (map.Scope != catalog.Scope || quantity <= 0 || !double.IsFinite(quantity))
            throw new InvalidDataException("Invalid native extraction energy request.");
        var drill = map.Prototypes[map.Items[drillItem].EntityName];
        double seconds = connection.ResourceIds.Select(id => map.Entities.Single(e => e.Id == id))
            .Select(e => Positive(map.Prototypes[e.Name].MiningTime) / Positive(drill.MiningSpeed)
                / catalog.Mining[e.Name].Single(p => p.Name == resourceItem && p.DeterministicItem).Amount!.Value).Max();
        double energy = quantity * seconds * 60 * Positive(drill.EnergyPerTick) / (drill.IsElectric ? 1 : Positive(drill.BurnerEffectivity));
        return double.IsFinite(energy) ? energy : throw new InvalidDataException("Native extraction work overflowed.");
        static double Positive(double? value) => value is { } n && n > 0 && double.IsFinite(n) ? n
            : throw new InvalidDataException("Missing native extraction time, speed, energy or efficiency.");
    }

    public IReadOnlyList<InstalledExtraction> FindInstalled(SpatialSnapshot map, string drillItem, string resourceItem,
        ProductionCatalog catalog, IReadOnlyList<SpatialEntity> receivers, IReadOnlySet<string> ownedIds)
    {
        var result = new List<InstalledExtraction>();
        string name = map.Items[drillItem].EntityName;
        foreach (SpatialEntity drill in map.Entities.Where(e => e.Name == name && ownedIds.Contains(e.Id) && e.DropPosition is not null))
        {
            SpatialEntity[] targets = receivers.Where(r => DropTile(drill.DropPosition!).Overlaps(r.Bounds)
                && (drill.DropTargetId is null || drill.DropTargetId == r.Id)).ToArray();
            if (targets.Length != 1) continue;
            var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != drill.Id).ToArray() });
            var connection = Find(field, drillItem, resourceItem, catalog, targets).FirstOrDefault(p => p.Drill.Position == drill.Position
                && p.Drill.Direction == drill.Direction && p.OutputPosition.DistanceTo(drill.DropPosition!) <= 0.01);
            if (connection is not null) result.Add(new(drill.Id, connection));
        }
        return result;
    }

    public ExtractionSite? FindNewSite(SpatialSnapshot map, string drillItem, string receiverItem, string resourceItem,
        ProductionCatalog catalog, CancellationToken token = default)
    {
        var field = new SpatialCollisionField(map);
        var geometry = map.Prototypes[map.Items[receiverItem].EntityName];
        var deposits = map.Entities.Where(e => e.Amount > 0 && catalog.Mining.TryGetValue(e.Name, out var products)
            && products.Length == 1 && products[0].Name == resourceItem && products[0].DeterministicItem)
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).Take(16);
        foreach (var deposit in deposits)
        {
            token.ThrowIfCancellationRequested();
            foreach (var placement in new PlacementPlanner().FindCandidates(field, receiverItem, deposit.Position, false, 32))
            {
                token.ThrowIfCancellationRequested();
                var receiver = new SpatialEntity("planned:receiver", geometry.Name, placement.Position,
                    geometry.CollisionBox.Rotate(placement.Direction).Translate(placement.Position), placement.Direction, "planned");
                var occupied = map with { Entities = [.. map.Entities, receiver] };
                var connection = Find(new(occupied), drillItem, resourceItem, catalog, [receiver], 1).FirstOrDefault();
                if (connection is not null) return new(placement, connection);
            }
        }
        return null;
    }

    public IReadOnlyList<ExtractionPlacement> Find(SpatialCollisionField field, string drillItem, string resourceItem,
        ProductionCatalog catalog, IReadOnlyList<SpatialEntity> receivers, int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        EntityGeometry drill = field.Map.Prototypes[field.Map.Items[drillItem].EntityName];
        if (drill.Type != "mining-drill" || drill.MiningOutput is not { } vector || drill.MiningRadius is not > 0
            || !double.IsFinite(drill.MiningRadius.Value) || !double.IsFinite(vector.X) || !double.IsFinite(vector.Y)
            || drill.ResourceCategories is null) throw new InvalidDataException("Missing native mining geometry.");
        SpatialEntity[] deposits = EligibleDeposits(field.Map, drill).ToArray();
        var result = new List<ExtractionPlacement>();
        foreach (SpatialEntity receiver in receivers)
        {
            for (int direction = 0; direction < 16; direction += 4)
            {
                // Map positions use the engine's 1/256-tile grid; prototype vectors
                // are floating point and can otherwise miss a touching receiver.
                MapPosition rotated = Rotate(vector, direction);
                MapPosition offset = new(Math.Truncate(rotated.X * 256) / 256, Math.Truncate(rotated.Y * 256) / 256);
                int width = direction % 8 == 0 ? drill.TileWidth : drill.TileHeight;
                int height = direction % 8 == 0 ? drill.TileHeight : drill.TileWidth;
                double alignX = width % 2 * 0.5, alignY = height % 2 * 0.5;
                for (double x = Math.Ceiling(Math.Floor(receiver.Bounds.Min.X) - offset.X - alignX) + alignX; x < Math.Ceiling(receiver.Bounds.Max.X) - offset.X; x++)
                    for (double y = Math.Ceiling(Math.Floor(receiver.Bounds.Min.Y) - offset.Y - alignY) + alignY; y < Math.Ceiling(receiver.Bounds.Max.Y) - offset.Y; y++)
                    {
                        var position = new MapPosition(x, y);
                        var output = new MapPosition(x + offset.X, y + offset.Y);
                        if (!DropTile(output).Overlaps(receiver.Bounds)) continue;
                        if (!field.PlacementClear(drill, position, direction)) continue;
                        double radius = drill.MiningRadius.Value;
                        var area = new WorldBox(new(x - radius, y - radius), new(x + radius, y + radius));
                        if (!field.Map.Bounds.Contains(area)) continue;
                        SpatialEntity[] covered = deposits.Where(e => area.Overlaps(e.Bounds)).ToArray();
                        if (!covered.Any(e => area.Contains(e.Position)) || covered.Any(e => !catalog.Mining.TryGetValue(e.Name, out var products)
                            || products.Length == 0 || products.Any(p => !p.DeterministicItem || p.Name != resourceItem))) continue;
                        result.Add(new(new(position, direction, position.DistanceTo(field.Map.Actor.Position)), receiver.Id,
                            output, covered.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray()));
                    }
            }
        }
        return result.OrderBy(p => p.Drill.Score).ThenBy(p => p.ReceiverId, StringComparer.Ordinal)
            .ThenBy(p => p.Drill.Direction).Take(limit).ToArray();
    }

    // Native drop_target uses intersection with the tile under the drop position,
    // not containment of the point in the receiving entity's collision box.
    public static WorldBox DropTile(MapPosition point) => new(new(Math.Floor(point.X), Math.Floor(point.Y)),
        new(Math.Floor(point.X) + 1, Math.Floor(point.Y) + 1));

    public static MapPosition Rotate(MapPosition vector, int direction) => direction switch
    {
        0 => vector,
        4 => new(-vector.Y, vector.X),
        8 => new(-vector.X, -vector.Y),
        12 => new(vector.Y, -vector.X),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}
