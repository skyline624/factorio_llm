namespace Factorio.Agent.Core;

/// <summary>A fluid a chain draws from outside its machines: extracted from a deposit, or pumped from terrain when Resource is null.</summary>
public sealed record FluidSource(string Fluid, string? Resource, double UnitsPerMinute);

/// <summary>
/// Persistent fluid-machine stages for a target rate, suppliers first. Solid raws are delivered by factory logistics;
/// extracted and terrain fluids come from pumpjack cells and offshore pumps.
/// </summary>
public sealed record FluidChainPlan(string Item, double PerMinute, IReadOnlyList<AutomationStage> Stages,
    IReadOnlyList<FluidSource> Sources, IReadOnlyDictionary<string, double> RawPerMinute);

/// <summary>
/// Chains recipes with fluid ingredients or products (plastic, sulfur, sulfuric acid, oil processing) from native recipe
/// amounts and machine crafting speeds. A fluid ingredient is supplied by another chain recipe, by a mined deposit, or
/// otherwise by terrain; solid products of the chain feed later stages through the actor like any cell output.
/// </summary>
public static class FluidChainPlanner
{
    public static FluidChainPlan? Plan(ProductionCatalog catalog, string item, double perMinute, int maximumMachinesPerStage = 8)
    {
        if (!double.IsFinite(perMinute) || perMinute <= 0 || perMinute > 100000) throw new ArgumentOutOfRangeException(nameof(perMinute));
        if (Choose(catalog, item) is null) return null;
        var crafts = new Dictionary<string, (NativeRecipe Recipe, string Machine, double Crafts)>(StringComparer.Ordinal);
        var order = new List<string>();
        var sources = new Dictionary<string, (string? Resource, double Units)>(StringComparer.Ordinal);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        Add(item, perMinute, []);
        var stages = order.Select(name =>
        {
            var (recipe, machine, needed) = crafts[name];
            double machineCrafts = 60 * catalog.Assemblers![machine].CraftingSpeed / recipe.EnergySeconds;
            double solidIn = recipe.Ingredients.Where(i => i.DeterministicItem).Sum(i => i.Amount!.Value);
            double solidOut = recipe.Products.Where(p => p.DeterministicItem).Sum(p => p.Amount!.Value);
            // Solids move through one basic inserter per side, as in assembler cells.
            double limit = Math.Max(solidIn, solidOut) > 0
                ? Math.Min(machineCrafts, 60 * AutomationPlanner.InserterItemsPerSecond / Math.Max(solidIn, solidOut)) : machineCrafts;
            int machines = (int)Math.Ceiling(needed / limit - 1e-9);
            return new AutomationStage(recipe.Name, recipe.Products[0].Name, machine, needed, Math.Clamp(machines, 1, maximumMachinesPerStage));
        }).ToArray();
        return new(item, perMinute, stages, sources.Select(p => new FluidSource(p.Key, p.Value.Resource, p.Value.Units))
            .OrderBy(s => s.Fluid, StringComparer.Ordinal).ToArray(), raw);

        void Add(string name, double rate, IReadOnlyList<string> path)
        {
            if (path.Contains(name)) throw new InvalidOperationException("Fluid chain recipes form a cycle: " + string.Join(" -> ", path.Append(name)));
            var choice = Choose(catalog, name);
            if (choice is null)
            {
                if (IsFluid(catalog, name))
                    sources[name] = (Resource(catalog, name), sources.GetValueOrDefault(name).Units + rate);
                else raw[name] = raw.GetValueOrDefault(name) + rate;
                return;
            }
            var (recipe, machine) = choice.Value;
            double recipeCrafts = rate / recipe.Products[0].Amount!.Value;
            foreach (var ingredient in recipe.Ingredients)
                Add(ingredient.Name, recipeCrafts * ingredient.Amount!.Value, [.. path, name]);
            // Post-order: every supplier stage precedes its consumers.
            if (!crafts.ContainsKey(recipe.Name)) order.Add(recipe.Name);
            crafts[recipe.Name] = (recipe, machine, crafts.GetValueOrDefault(recipe.Name).Crafts + recipeCrafts);
        }
    }

    /// <summary>
    /// The enabled single-product recipe of a product that moves fluids, with the fastest fluid machine accepting it, whose
    /// every fluid ingredient can itself be supplied. Solid-only recipes are left to the assembler planner.
    /// </summary>
    public static (NativeRecipe Recipe, string MachineItem)? Choose(ProductionCatalog catalog, string product) =>
        Choose(catalog, product, []);

    private static (NativeRecipe Recipe, string MachineItem)? Choose(ProductionCatalog catalog, string product, IReadOnlyList<string> path)
    {
        if (path.Contains(product) || path.Count > 16) return null;
        var machines = (catalog.Assemblers ?? new Dictionary<string, NativeAssembler>())
            .Where(p => p.Value.FixedRecipe is null && (p.Value.FluidInputCount > 0 || p.Value.FluidOutputCount > 0))
            .OrderByDescending(p => p.Value.CraftingSpeed).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        foreach (var recipe in catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == product
                && (r.Products[0].DeterministicItem || r.Products[0].DeterministicFluid) && r.Ingredients.Count > 0
                && r.Ingredients.All(i => i.DeterministicItem || i.DeterministicFluid)
                && (r.Products[0].DeterministicFluid || r.Ingredients.Any(i => i.DeterministicFluid))
                && r.Ingredients.All(i => i.Name != product))
            .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var machine = machines.FirstOrDefault(p => p.Value.Accepts(recipe));
            if (machine.Key is null) continue;
            if (recipe.Ingredients.Where(i => i.DeterministicFluid).All(i => Resource(catalog, i.Name) is not null
                || Choose(catalog, i.Name, [.. path, product]) is not null || Terrain(catalog, i.Name)))
                return (recipe, machine.Key);
        }
        return null;
    }

    /// <summary>
    /// Native output of one extractor on a deposit: speed times product per cycle over mining time, scaled by the
    /// deposit yield (amount over normal amount) when the resource is infinite.
    /// </summary>
    public static double ExtractorPerMinute(EntityGeometry extractor, EntityGeometry resource, double amount, NativeMaterial product)
    {
        if (extractor.MiningSpeed is not > 0 || resource.MiningTime is not > 0 || product.Amount is not > 0 || !double.IsFinite(amount) || amount <= 0)
            throw new InvalidDataException("Extraction rate requires native mining speed, mining time, product amount and a positive deposit.");
        double yield = resource.InfiniteResource
            ? amount / (resource.NormalResourceAmount is > 0 ? resource.NormalResourceAmount.Value
                : throw new InvalidDataException("An infinite deposit requires its native normal amount."))
            : 1;
        return 60 * extractor.MiningSpeed.Value * product.Amount.Value * yield / resource.MiningTime.Value;
    }

    /// <summary>The deposit whose mining yields this fluid, if any.</summary>
    public static string? Resource(ProductionCatalog catalog, string fluid) => catalog.Mining
        .Where(p => p.Value.Any(m => m.Name == fluid && m.DeterministicFluid)).Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>A fluid native tiles hold, such as base-game water: offshore pumps draw it from terrain.</summary>
    public static bool Terrain(ProductionCatalog catalog, string fluid) => catalog.TerrainFluids?.Contains(fluid) == true;

    private static bool IsFluid(ProductionCatalog catalog, string name) =>
        catalog.Recipes.SelectMany(r => r.Ingredients.Concat(r.Products)).Any(m => m.Name == name && m.Type == "fluid");
}
