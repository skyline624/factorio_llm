namespace Factorio.Agent.Core;

public sealed record RocketStep(string Kind, int RemainingCycles, IReadOnlyDictionary<string, int> RequiredItems);

/// <summary>Uses native part counters and launch phases; a prepared rocket is never interpreted as a completed launch.</summary>
public static class RocketPlanner
{
    /// <summary>At most five cycles per delivery, reduced by loaded stock and the engine's insertable counts.</summary>
    public static IReadOnlyDictionary<string, int> SupplyBatch(RocketSiloPrototype prototype, ObservedRocketSilo silo, NativeRecipe recipe)
    {
        RocketStep step = Next(prototype, silo, recipe);
        if (step.Kind != "supply") return new Dictionary<string, int>();
        return recipe.Ingredients.GroupBy(p => p.Name).ToDictionary(g => g.Key, g =>
        {
            double missing = Math.Max(0, Math.Ceiling(g.Sum(p => p.Amount!.Value) * 5) - silo.Inputs.GetValueOrDefault(g.Key));
            return checked((int)Math.Min(1000, Math.Min(step.RequiredItems[g.Key],
                Math.Min(missing, silo.Insertable.GetValueOrDefault(g.Key)))));
        }, StringComparer.Ordinal);
    }

    public static RocketStep Next(RocketSiloPrototype prototype, ObservedRocketSilo silo, NativeRecipe recipe)
    {
        if (silo.Name != prototype.EntityName || silo.Recipe != prototype.Recipe || recipe.Name != prototype.Recipe
            || prototype.PartsRequired < 1 || silo.Parts < 0 || silo.Parts > prototype.PartsRequired
            || !RocketSnapshot.Statuses.Contains(silo.Status) || silo.Inputs.Values.Any(n => n < 0))
            throw new InvalidDataException("Inconsistent native rocket planning inputs.");
        if (silo.Status == "rocket_ready")
        {
            if (!silo.RocketPresent) throw new InvalidDataException("Ready silo has no native rocket entity.");
            return new("launch", 0, new Dictionary<string, int>());
        }
        // The engine can reset rocket_parts while opening the silo or launching. Do not refill during these phases.
        if (silo.Status != "building_rocket" || silo.Parts == prototype.PartsRequired)
            return new("wait", 0, new Dictionary<string, int>());
        if (!recipe.Enabled || recipe.Products.Count != 1 || !recipe.Products[0].DeterministicItem
            || recipe.Ingredients.Any(p => !p.DeterministicItem))
            throw new InvalidOperationException("Rocket part supply requires an enabled deterministic solid recipe.");
        int cycles = checked((int)Math.Ceiling((prototype.PartsRequired - silo.Parts) / recipe.Products[0].Amount!.Value));
        int unstarted = Math.Max(0, cycles - (silo.InProcess ? 1 : 0));
        var needed = recipe.Ingredients.GroupBy(p => p.Name).ToDictionary(g => g.Key,
            g => checked((int)Math.Ceiling(Math.Max(0, unstarted * g.Sum(p => p.Amount!.Value) - silo.Inputs.GetValueOrDefault(g.Key)))),
            StringComparer.Ordinal);
        return new(needed.Values.Any(n => n > 0) ? "supply" : "wait", cycles, needed);
    }
}
