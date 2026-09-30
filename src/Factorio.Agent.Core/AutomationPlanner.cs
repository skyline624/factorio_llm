namespace Factorio.Agent.Core;

public sealed record AutomationStage(string Recipe, string Item, string MachineItem, double CraftsPerMinute, int Machines);
/// <summary>Assembler stages for a target rate; raw inputs (plates, ores, fluids, unsupported items) are supplied by the actor.</summary>
public sealed record AutomationPlan(IReadOnlyList<AutomationStage> Stages, IReadOnlyDictionary<string, double> RawPerMinute);

/// <summary>Sizes chest-fed assembler cells from native recipe amounts, crafting speed and one basic inserter per side.</summary>
public static class AutomationPlanner
{
    /// <summary>Observed native throughput of one basic inserter between a chest and a machine.</summary>
    public const double InserterItemsPerSecond = 0.8;

    public static AutomationPlan Plan(ProductionCatalog catalog, string item, double perMinute, IReadOnlySet<string> machineItems,
        int maximumMachinesPerStage = 8)
    {
        if (!double.IsFinite(perMinute) || perMinute <= 0 || perMinute > 10000) throw new ArgumentOutOfRangeException(nameof(perMinute));
        var crafts = new Dictionary<string, (NativeRecipe Recipe, string Machine, double Crafts)>(StringComparer.Ordinal);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        Add(item, perMinute, []);
        var stages = crafts.Values.Select(stage =>
        {
            double speed = catalog.Assemblers![stage.Machine].CraftingSpeed;
            double machineCrafts = 60 * speed / stage.Recipe.EnergySeconds;
            double inputs = stage.Recipe.Ingredients.Where(i => i.DeterministicItem).Sum(i => i.Amount!.Value);
            double outputs = stage.Recipe.Products.Sum(p => p.Amount!.Value);
            double armCrafts = 60 * InserterItemsPerSecond / Math.Max(inputs, outputs);
            int machines = (int)Math.Ceiling(stage.Crafts / Math.Min(machineCrafts, armCrafts) - 1e-9);
            return new AutomationStage(stage.Recipe.Name, stage.Recipe.Products[0].Name, stage.Machine, stage.Crafts,
                Math.Clamp(machines, 1, maximumMachinesPerStage));
        }).OrderBy(s => s.Recipe, StringComparer.Ordinal).ToArray();
        return new(stages, raw);

        void Add(string name, double rate, IReadOnlyList<string> path)
        {
            if (path.Contains(name)) throw new InvalidOperationException("Automation recipes form a cycle: " + string.Join(" -> ", path.Append(name)));
            var choice = Choose(catalog, name, machineItems);
            if (choice is null)
            {
                raw[name] = raw.GetValueOrDefault(name) + rate;
                return;
            }
            var (recipe, machine) = choice.Value;
            double recipeCrafts = rate / recipe.Products[0].Amount!.Value;
            var previous = crafts.GetValueOrDefault(recipe.Name);
            crafts[recipe.Name] = (recipe, machine, previous.Crafts + recipeCrafts);
            foreach (var ingredient in recipe.Ingredients)
                Add(ingredient.Name, recipeCrafts * ingredient.Amount!.Value, [.. path, name]);
        }
    }

    /// <summary>
    /// The enabled single-product recipe of an item that an available assembler can craft from solids only.
    /// Smelted, mined and fluid materials are left to other suppliers.
    /// </summary>
    public static (NativeRecipe Recipe, string MachineItem)? Choose(ProductionCatalog catalog, string item, IReadOnlySet<string> machineItems)
    {
        var assemblers = (catalog.Assemblers ?? new Dictionary<string, NativeAssembler>())
            .Where(p => machineItems.Contains(p.Key)).OrderByDescending(p => p.Value.CraftingSpeed).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        foreach (var recipe in catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == item
                && r.Products[0].DeterministicItem && r.Ingredients.Count > 0 && r.Ingredients.All(i => i.DeterministicItem))
            .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var machine = assemblers.FirstOrDefault(p => p.Value.Accepts(recipe) && p.Value.FixedRecipe is null);
            if (machine.Key is not null) return (recipe, machine.Key);
        }
        return null;
    }
}
