namespace Factorio.Agent.Core;

public sealed record LaboratoryExtension(PlacementCandidate Pole, PlacementCandidate Lab);

public sealed class LaboratoryPlanner
{
    public LaboratoryExtension? Extend(SpatialSnapshot map, string labItem, string poleItem, SpatialEntity source)
    {
        EntityGeometry pole = map.Prototypes[map.Items[poleItem].EntityName];
        double range = Math.Min(pole.MaxWireDistance ?? 0, map.Prototypes[source.Name].MaxWireDistance ?? 0);
        if (source.Power?.NetworkId is null || range <= 0) return null;
        foreach (var candidate in new PlacementPlanner().FindCandidates(new(map), poleItem, source.Position, requireBuildReach: false)
            .Where(p => p.Position.DistanceTo(source.Position) <= range))
        {
            var proposed = new SpatialEntity("planned:lab-pole", pole.Name, candidate.Position,
                pole.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position), candidate.Direction, source.Force, Power: source.Power);
            SpatialSnapshot extended = map with { Entities = [.. map.Entities, proposed] };
            PlacementCandidate? lab = Place(extended, labItem, proposed);
            if (lab is not null) return new(candidate, lab);
        }
        return null;
    }

    public static IReadOnlyDictionary<string, int> RequiredPacks(NativeTechnology technology, double progress,
        IReadOnlyDictionary<string, double> effectiveStock)
    {
        if (!double.IsFinite(progress) || progress is < 0 or > 1
            || effectiveStock.Values.Any(n => !double.IsFinite(n) || n < 0))
            throw new InvalidDataException("Invalid native research progress or science durability.");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (technology.Researched) return result;
        if (technology.Trigger is not null || technology.Count <= 0 || technology.Ingredients.Count == 0)
            throw new InvalidDataException("The technology does not have laboratory science requirements.");
        foreach (var ingredient in technology.Ingredients.GroupBy(i => i.Name))
        {
            double amount = ingredient.Sum(i => i.Amount);
            double missing = Math.Ceiling(technology.Count * (1 - progress) * amount - effectiveStock.GetValueOrDefault(ingredient.Key) - 1e-9);
            if (!double.IsFinite(amount) || amount <= 0 || !double.IsFinite(missing) || missing > 1000)
                throw new InvalidDataException("Science requirements exceed the supported batch budget.");
            result[ingredient.Key] = (int)Math.Max(0, missing);
        }
        return result;
    }

    public PlacementCandidate? Place(SpatialSnapshot map, string labItem, SpatialEntity pole)
    {
        if (pole.Power?.NetworkId is null) return null;
        EntityGeometry poleGeometry = map.Prototypes[pole.Name];
        if (poleGeometry.SupplyArea is not > 0) return null;
        EntityGeometry lab = map.Prototypes[map.Items[labItem].EntityName];
        if (lab.Type != "lab") throw new InvalidDataException("Expected native lab placement geometry.");
        double radius = poleGeometry.SupplyArea.Value;
        var coverage = new WorldBox(new(pole.Position.X - radius, pole.Position.Y - radius), new(pole.Position.X + radius, pole.Position.Y + radius));
        return new PlacementPlanner().FindCandidates(new(map), labItem, pole.Position, requireBuildReach: false)
            .FirstOrDefault(p => coverage.Overlaps(lab.CollisionBox.Rotate(p.Direction).Translate(p.Position)));
    }
}
