using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed record ScienceIngredient(string Name, double Amount);

public sealed record NativeTechnology(string Name, bool Enabled, bool Researched, bool Available,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Prerequisites,
    [property: JsonConverter(typeof(NativeArrayConverter<ScienceIngredient>))] IReadOnlyList<ScienceIngredient> Ingredients,
    long Count, double EnergyTicks, JsonElement? Trigger = null, JsonElement? Effects = null);

public sealed record TechnologyStep(string Kind, string Technology, string? Item = null, int Count = 0, string? Reason = null, string? Entity = null);

/// <summary>Resolves native technology dependencies without granting research or guessing unlocks.</summary>
public sealed class TechnologyPlanner
{
    public TechnologyStep Next(string target, IReadOnlyDictionary<string, NativeTechnology> technologies)
    {
        var path = new HashSet<string>(StringComparer.Ordinal);
        return Need(target);

        TechnologyStep Need(string name)
        {
            if (!technologies.TryGetValue(name, out NativeTechnology? technology))
                throw new InvalidDataException($"Missing native technology {name} in the dependency observation.");
            if (technology.Researched) return new("completed", name);
            if (!path.Add(name) || path.Count > 256) throw new InvalidDataException("Cyclic or excessive technology dependency graph.");
            if (!technology.Enabled) return Unsupported("The native technology is disabled.");
            foreach (string prerequisite in technology.Prerequisites.Order(StringComparer.Ordinal))
            {
                if (!technologies.TryGetValue(prerequisite, out NativeTechnology? dependency))
                    throw new InvalidDataException($"Missing native prerequisite {prerequisite}.");
                if (!dependency.Researched) return Need(prerequisite);
            }
            if (!technology.Available) return Unsupported("The native technology is not currently available.");
            if (technology.Trigger is { } trigger && trigger.ValueKind != JsonValueKind.Null)
            {
                if (trigger.ValueKind != JsonValueKind.Object
                    || !trigger.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
                    return Unsupported("This native research trigger is not implemented.");
                if (type.GetString() == "mine-entity")
                {
                    if (!trigger.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(entity.GetString())) return Unsupported("The native mining trigger has no exact entity identity.");
                    return new("mine-trigger", name, Count: 1, Entity: entity.GetString());
                }
                if (type.GetString() != "craft-item") return Unsupported("This native research trigger is not implemented.");
                if (!trigger.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out JsonElement itemName) || itemName.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(itemName.GetString()) || !trigger.TryGetProperty("count", out JsonElement count)
                    || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int quantity) || quantity is < 1 or > 1000)
                    return Unsupported("The native craft trigger has an invalid or unsupported item count.");
                return new("craft-trigger", name, itemName.GetString(), quantity);
            }
            if (technology.Count <= 0 || technology.Ingredients.Count == 0
                || technology.Ingredients.Any(i => !double.IsFinite(i.Amount) || i.Amount <= 0 || string.IsNullOrWhiteSpace(i.Name)))
                return Unsupported("The technology has no supported native science requirements.");
            return new("research", name);

            TechnologyStep Unsupported(string reason) => new("unsupported", name, Reason: reason);
        }
    }
}
