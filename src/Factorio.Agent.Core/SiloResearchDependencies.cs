using System.Text.Json;

namespace Factorio.Agent.Core;

/// <summary>Observed technology edges for silo recipes; not a production or launch feasibility plan.</summary>
public sealed record SiloResearchDependencies(string[] SiloRecipes, string[] UnlockTechnologies,
    IReadOnlyDictionary<string, string[]> Prerequisites, string[] RemainingTechnologies,
    string[] AvailableResearch, string[] DisabledTechnologies)
{
    public string Interpretation => "Native prerequisite edges for technologies unlocking observed silo recipes. " +
        "Other research may support survival or production. This graph does not establish ingredient, science, power or launch readiness. " +
        "An empty unlock list means no matching native unlock effect was observed, not that research is complete.";

    public static SiloResearchDependencies Read(ProductionCatalog catalog, IReadOnlyDictionary<string, NativeTechnology> technologies)
    {
        var siloItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "rocket-silo")
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        string[] recipes = catalog.Recipes.Where(r => r.Products.Any(p => p.DeterministicItem && siloItems.Contains(p.Name)))
            .Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();
        string[] roots = technologies.Values.Where(t => UnlocksSilo(t.Effects)).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        var dependencies = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in roots) Visit(root);
        string[] remaining = dependencies.Keys.Where(n => !technologies[n].Researched).ToArray();
        return new(recipes, roots, dependencies, remaining,
            remaining.Where(n => technologies[n].Enabled && technologies[n].Available).ToArray(),
            remaining.Where(n => !technologies[n].Enabled).ToArray());

        bool UnlocksSilo(JsonElement? effects) => effects is { ValueKind: JsonValueKind.Array } array
            && array.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "unlock-recipe"
                && e.TryGetProperty("recipe", out var recipe) && recipe.ValueKind == JsonValueKind.String
                && recipes.Contains(recipe.GetString(), StringComparer.Ordinal));

        void Visit(string name)
        {
            if (visiting.Contains(name)) throw new InvalidDataException("Cyclic native silo research dependencies.");
            if (dependencies.ContainsKey(name)) return;
            if (!technologies.TryGetValue(name, out var technology) || technology.Name != name)
                throw new InvalidDataException($"Missing or mismatched native silo research dependency {name}.");
            if (dependencies.Count >= 256) throw new InvalidDataException("Native silo research dependency graph exceeds its budget.");
            visiting.Add(name);
            string[] prerequisites = technology.Prerequisites.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            dependencies.Add(name, prerequisites);
            foreach (string prerequisite in prerequisites) Visit(prerequisite);
            visiting.Remove(name);
        }
    }
}
