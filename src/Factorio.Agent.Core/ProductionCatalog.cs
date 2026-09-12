using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed record NativeMaterial(string Name, string Type, double? Amount = null, double? AmountMin = null,
    double? AmountMax = null, double? Probability = null, double? Temperature = null,
    double? MinimumTemperature = null, double? MaximumTemperature = null)
{
    [JsonIgnore]
    public bool DeterministicFluid => Type == "fluid" && Amount is > 0 && double.IsFinite(Amount.Value) && Probability is null or 1;

    [JsonIgnore]
    public bool DeterministicItem => Type == "item" && Amount is > 0
        && double.IsFinite(Amount.Value) && Amount == Math.Truncate(Amount.Value) && Probability is null or 1;
}
public sealed record NativeRecipe(string Name, bool Enabled, string Category, double EnergySeconds,
    [property: JsonConverter(typeof(NativeArrayConverter<NativeMaterial>))] IReadOnlyList<NativeMaterial> Ingredients,
    [property: JsonConverter(typeof(NativeArrayConverter<NativeMaterial>))] IReadOnlyList<NativeMaterial> Products,
    bool HandCraftingDisabled);
public sealed record NativeItem(double FuelValue, int StackSize, string? FuelCategory = null, string? PlaceEntity = null, string? PlaceEntityType = null);
public sealed record NativeFurnace(string EntityName, IReadOnlyDictionary<string, bool> Categories,
    IReadOnlyDictionary<string, bool> FuelCategories, double CraftingSpeed);
public sealed record ProductionCatalog(ActorScope Scope, long CollectedTick,
    [property: JsonConverter(typeof(NativeArrayConverter<NativeRecipe>))] IReadOnlyList<NativeRecipe> Recipes,
    IReadOnlyDictionary<string, NativeItem> Items, IReadOnlyDictionary<string, NativeMaterial[]> Mining,
    IReadOnlyDictionary<string, NativeFurnace> Machines, IReadOnlyDictionary<string, bool> HandCategories,
    IReadOnlyDictionary<string, NativeAssembler>? Assemblers = null)
{
    public bool CanHandCraft(NativeRecipe recipe) => !recipe.HandCraftingDisabled && HandCategories.ContainsKey(recipe.Category);

    public static ProductionCatalog Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error!);
        ProductionCatalog catalog = response.Data.Deserialize<ProductionCatalog>(Protocol.Json)
            ?? throw new InvalidDataException("Missing native production catalog.");
        if (catalog.CollectedTick != response.Tick || catalog.Recipes.Count > 10000
            || catalog.Recipes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Recipes.Count
            || catalog.Recipes.Any(r => !double.IsFinite(r.EnergySeconds) || r.EnergySeconds <= 0)
            || catalog.Items.Values.Any(i => !double.IsFinite(i.FuelValue) || i.FuelValue < 0 || i.StackSize < 1)
            || catalog.Machines.Values.Any(m => !double.IsFinite(m.CraftingSpeed) || m.CraftingSpeed <= 0)
            || catalog.Assemblers?.Values.Any(m => !double.IsFinite(m.CraftingSpeed) || m.CraftingSpeed <= 0
                || !double.IsFinite(m.EnergyPerTick) || m.EnergyPerTick <= 0) == true)
            throw new InvalidDataException("Inconsistent native production catalog.");
        return catalog;
    }
}

public sealed record ProductionStep(string Kind, string Item, int Quantity, NativeRecipe? Recipe = null,
    SpatialEntity? Source = null, string? Reason = null);

public sealed record KnownProductionMachine(string Id, string Name, string? Recipe,
    IReadOnlyDictionary<string, long>? Input = null, IReadOnlyDictionary<string, long>? Output = null)
{
    public bool CanProcess(NativeRecipe recipe) => (Recipe is null || Recipe == recipe.Name)
        && (Input is null || Input.All(p => p.Value == 0 || recipe.Ingredients.Any(i => i.Name == p.Key)))
        && (Output is null || Output.All(p => p.Value == 0 || recipe.Products.Any(i => i.Name == p.Key)));
}

/// <summary>Selects one bounded next step, re-evaluated against actual stock after every operation.</summary>
public sealed class ProductionPlanner
{
    public ProductionStep Next(string item, int targetStock, IReadOnlyDictionary<string, long> inventory,
        ProductionCatalog catalog, SpatialSnapshot map, IReadOnlyList<KnownProductionMachine> machines)
    {
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var knownMachines = machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return Need(item, targetStock);

        ProductionStep Need(string wanted, int total)
        {
            long missing = total - inventory.GetValueOrDefault(wanted);
            if (missing <= 0) return new("satisfied", wanted, total);
            if (missing > 1000 || !visiting.Add(wanted)) return new("unsupported", wanted, total, Reason: "Dependency cycle or batch exceeds 1000 items.");
            try
            {
                string[] sources = catalog.Mining.Where(p => p.Value.Any(m => m.Name == wanted && m.DeterministicItem))
                    .Select(p => p.Key).ToArray();
                if (sources.Length > 0)
                {
                    SpatialEntity? source = map.Entities.Where(e => sources.Contains(e.Name) && e.Amount is null or > 0)
                        .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
                    return source is null
                        ? new("unavailable", wanted, (int)missing, Reason: "No currently observed extraction source; exploration is required.")
                        : new("mine", wanted, (int)missing, Source: source);
                }
                if (catalog.Assemblers is { } assemblers)
                {
                    var assembly = new AssemblyPlanner().Choose(wanted, catalog.Recipes, assemblers, machines, configuredOnly: true)
                        ?? new AssemblyPlanner().Choose(wanted, catalog.Recipes.Where(r => !catalog.CanHandCraft(r)), assemblers, machines);
                    if (assembly is not null) return new("assemble", wanted, total, assembly.Recipe);
                }
                NativeRecipe[] candidates = catalog.Recipes.Where(r => r.Enabled
                    && r.Products.Any(p => p.Name == wanted) && r.Products.All(p => p.DeterministicItem)
                    && r.Ingredients.All(p => p.DeterministicItem)
                    && (catalog.CanHandCraft(r) || catalog.Machines.Values.Any(m => m.Categories.ContainsKey(r.Category))))
                    .OrderByDescending(catalog.CanHandCraft).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
                if (candidates.Length == 0) return new("unsupported", wanted, (int)missing, Reason: "No enabled deterministic solid recipe with a supported machine.");
                if (new SmeltingPlanner().Find(wanted, catalog, map, inventory, knownMachines) is { } automated)
                    return new("automate", wanted, total, automated.Recipe);
                ProductionStep? failed = null;
                foreach (NativeRecipe recipe in candidates)
                {
                    if (!catalog.CanHandCraft(recipe))
                    {
                        var supported = catalog.Machines.Where(m => m.Value.Categories.ContainsKey(recipe.Category))
                            .OrderByDescending(m => inventory.GetValueOrDefault(m.Key) > 0)
                            .ThenBy(m => m.Key, StringComparer.Ordinal).ToArray();
                        bool installed = machines.Any(e => e.CanProcess(recipe)
                            && supported.Any(m => m.Value.EntityName == e.Name));
                        if (!installed)
                        {
                            ProductionStep? prerequisite = null;
                            foreach (var machine in supported)
                            {
                                if (inventory.GetValueOrDefault(machine.Key) > 0) return new("build", machine.Key, 1);
                                ProductionStep next = Need(machine.Key, 1);
                                if (next.Kind is not ("unsupported" or "unavailable")) return next;
                                if (prerequisite is null || next.Kind == "unavailable") prerequisite = next;
                            }
                            failed = prerequisite ?? new("unsupported", wanted, total, Reason: "No producible compatible furnace.");
                            continue;
                        }
                    }
                    double yield = recipe.Products.Where(p => p.Name == wanted).Sum(p => p.Amount!.Value);
                    int batches = checked((int)Math.Ceiling(missing / yield));
                    if (!catalog.CanHandCraft(recipe)) batches = Math.Min(16, batches);
                    bool impossible = false;
                    foreach (var ingredient in recipe.Ingredients.GroupBy(p => p.Name))
                    {
                        int needed = checked((int)(ingredient.Sum(p => p.Amount!.Value) * batches));
                        ProductionStep dependency = Need(ingredient.Key, needed);
                        if (dependency.Kind is "unsupported" or "unavailable") { failed = dependency; impossible = true; break; }
                        if (dependency.Kind != "satisfied") return dependency;
                    }
                    if (!impossible) return new(catalog.CanHandCraft(recipe) ? "craft" : "smelt", wanted, batches, recipe);
                }
                return failed!;
            }
            finally { visiting.Remove(wanted); }
        }
    }
}
