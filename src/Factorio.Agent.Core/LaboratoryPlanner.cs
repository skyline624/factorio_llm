namespace Factorio.Agent.Core;

public sealed class LaboratoryPlanner
{
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

}
