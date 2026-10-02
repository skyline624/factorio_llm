namespace Factorio.Agent.Core;

public sealed record ConstructionStock(string Item, int TargetStock);
public sealed record ConstructionSupplyPlan(IReadOnlyList<ConstructionStock> Materials, IReadOnlyList<ConstructionStock> Equipment);

/// <summary>
/// Aggregates one construction kit's handcraft ingredients, sharing carried stock and native batch surplus.
/// Smelting, extraction and observed outputs remain stock goals for the ordinary production executor.
/// </summary>
public static class ConstructionSupplyPlanner
{
    public static ConstructionSupplyPlan? Plan(ProductionCatalog catalog, IReadOnlyDictionary<string, int> needed,
        IReadOnlyDictionary<string, long> carried, IReadOnlySet<string>? storedOutputs = null)
    {
        if (needed.Count > 64 || needed.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < 1 or > 1000))
            throw new ArgumentOutOfRangeException(nameof(needed));
        var stock = new Dictionary<string, long>(carried, StringComparer.Ordinal);
        var additional = new Dictionary<string, long>(StringComparer.Ordinal);
        var recipes = new Dictionary<string, NativeRecipe?>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        int visits = 0;
        try
        {
            // A required furnace may also be an ingredient of a required drill: build its consumer first.
            var equipment = needed.OrderByDescending(p => Depth(p.Key, 0)).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new ConstructionStock(p.Key, p.Value)).ToArray();
            foreach (var goal in equipment) Need(goal.Item, goal.TargetStock, 0);
            var materials = additional.Select(p => new ConstructionStock(p.Key, Bounded(carried.GetValueOrDefault(p.Key) + p.Value)))
                .OrderByDescending(p => Depth(p.Item, 0)).ThenBy(p => p.Item, StringComparer.Ordinal).ToArray();
            return new(materials, equipment);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            // Keep ordinary bounded stock procurement for a cyclic or oversized kit; never truncate its needs.
            return null;
        }

        NativeRecipe? Recipe(string item)
        {
            if (recipes.TryGetValue(item, out var known)) return known;
            var options = catalog.Recipes.Where(r => r.Enabled && catalog.CanHandCraft(r)
                && r.Products.Count > 0 && r.Products.All(p => p.Name == item && p.DeterministicItem)
                && r.Ingredients.All(p => p.DeterministicItem)).Take(2).ToArray();
            // Ambiguous recipes are left to the existing executor rather than guessed by the kit estimate.
            return recipes[item] = options.Length == 1 ? options[0] : null;
        }

        int Depth(string item, int level)
        {
            if (depths.TryGetValue(item, out int known)) return known;
            Visit(level);
            if (!active.Add(item)) throw new InvalidDataException("Cyclic construction ingredients.");
            try
            {
                var recipe = Recipe(item);
                return depths[item] = recipe is null ? 0 : 1 + recipe.Ingredients
                    .Select(p => Depth(p.Name, level + 1)).DefaultIfEmpty(0).Max();
            }
            finally { active.Remove(item); }
        }

        void Need(string item, long count, int level)
        {
            Visit(level);
            long used = Math.Min(Math.Max(0, stock.GetValueOrDefault(item)), count);
            stock[item] = stock.GetValueOrDefault(item) - used;
            long missing = count - used;
            if (missing == 0) return;
            var recipe = Recipe(item);
            if (recipe is null || storedOutputs?.Contains(item) == true
                || catalog.Mining.Values.Any(products => products.Any(p => p.Name == item && p.DeterministicItem)))
            {
                additional[item] = checked(additional.GetValueOrDefault(item) + missing);
                Bounded(checked(carried.GetValueOrDefault(item) + additional[item]));
                return;
            }
            long yield = checked((long)recipe.Products.Sum(p => p.Amount!.Value));
            long batches = checked((long)Math.Ceiling((double)missing / yield));
            foreach (var group in recipe.Ingredients.GroupBy(p => p.Name, StringComparer.Ordinal))
                Need(group.Key, checked((long)group.Sum(p => p.Amount!.Value) * batches), level + 1);
            stock[item] = checked(stock.GetValueOrDefault(item) + batches * yield - missing);
        }

        void Visit(int level)
        {
            if (level > 16 || ++visits > 2048) throw new InvalidDataException("Construction supply planning budget exceeded.");
        }
        static int Bounded(long count) => count is >= 1 and <= 1000 ? (int)count
            : throw new InvalidDataException("Construction material stock exceeds the executor's bound.");
    }
}
