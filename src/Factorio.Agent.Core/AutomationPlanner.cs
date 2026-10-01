namespace Factorio.Agent.Core;

/// <summary>Kind is the cell kind that serves the stage: "assembler", "furnace" for chest-fed burner furnaces, or "silo" for rocket parts.</summary>
public sealed record AutomationStage(string Recipe, string Item, string MachineItem, double CraftsPerMinute, int Machines, string Kind = "assembler");
/// <summary>Assembler, furnace and silo stages for a target rate; raw inputs (ore plates, ores, fluids, unsupported items) are supplied otherwise.</summary>
public sealed record AutomationPlan(IReadOnlyList<AutomationStage> Stages, IReadOnlyDictionary<string, double> RawPerMinute);

/// <summary>Sizes chest-fed assembler and furnace cells from native recipe amounts, crafting speed and one basic inserter per side.</summary>
public static class AutomationPlanner
{
    /// <summary>Observed native throughput of one basic inserter between a chest and a machine.</summary>
    public const double InserterItemsPerSecond = 0.8;

    public static AutomationPlan Plan(ProductionCatalog catalog, string item, double perMinute, IReadOnlySet<string> machineItems,
        int maximumMachinesPerStage = 8) => Plan(catalog, new Dictionary<string, double>(StringComparer.Ordinal) { [item] = perMinute },
            machineItems, maximumMachinesPerStage);

    /// <summary>Several targets share their intermediate stages: crafts add up per recipe before machines are counted.</summary>
    public static AutomationPlan Plan(ProductionCatalog catalog, IReadOnlyDictionary<string, double> targets, IReadOnlySet<string> machineItems,
        int maximumMachinesPerStage = 8)
    {
        if (targets.Count == 0 || targets.Values.Any(rate => !double.IsFinite(rate) || rate <= 0 || rate > 10000))
            throw new ArgumentOutOfRangeException(nameof(targets));
        var crafts = new Dictionary<string, (NativeRecipe Recipe, string Machine, double Crafts)>(StringComparer.Ordinal);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (item, perMinute) in targets.OrderBy(p => p.Key, StringComparer.Ordinal)) Add(item, perMinute, []);
        var stages = crafts.Values.Select(stage =>
        {
            string kind = Kind(catalog, stage.Machine);
            int machines = (int)Math.Ceiling(stage.Crafts / CellCraftsPerMinute(catalog, stage.Recipe, stage.Machine) - 1e-9);
            return new AutomationStage(stage.Recipe.Name, stage.Recipe.Products[0].Name, stage.Machine, stage.Crafts,
                Math.Clamp(machines, 1, Maximum(kind, maximumMachinesPerStage)), kind);
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

    /// <summary>Crafts per minute one cell of this machine gives: native crafting speed, capped by one basic inserter per side.</summary>
    public static double CellCraftsPerMinute(ProductionCatalog catalog, NativeRecipe recipe, string machineItem)
    {
        double speed = Kind(catalog, machineItem) switch
        {
            SiloCellPlanner.Kind => catalog.Silos![machineItem].CraftingSpeed,
            FurnaceCellPlanner.Kind => catalog.Machines[machineItem].CraftingSpeed,
            _ => catalog.Assemblers![machineItem].CraftingSpeed
        };
        double machineCrafts = 60 * speed / recipe.EnergySeconds;
        // A furnace's input arm also carries its fuel, a small share next to the ingredient (0.36 coal per steel craft).
        double inputs = recipe.Ingredients.Where(i => i.DeterministicItem).Sum(i => i.Amount!.Value);
        double outputs = recipe.Products.Sum(p => p.Amount!.Value);
        return Math.Min(machineCrafts, 60 * InserterItemsPerSecond / Math.Max(inputs, outputs));
    }

    /// <summary>
    /// Cells to add so the ready cells of the stage's recipe cover its crafts. Each ready cell counts with its own machine,
    /// so slower early cells are not mistaken for the faster machines a new plan sizes; the per-stage budget still applies.
    /// </summary>
    public static int MissingMachines(ProductionCatalog catalog, AutomationStage stage, IReadOnlyList<string> readyMachineItems,
        int maximumMachinesPerStage = 8)
    {
        var recipe = catalog.Recipes.Single(r => r.Name == stage.Recipe);
        double missing = stage.CraftsPerMinute - readyMachineItems.Sum(machine => CellCraftsPerMinute(catalog, recipe, machine));
        int wanted = missing <= 1e-9 ? 0 : (int)Math.Ceiling(missing / CellCraftsPerMinute(catalog, recipe, stage.MachineItem) - 1e-9);
        return Math.Clamp(wanted, 0, Math.Max(0, Maximum(stage.Kind, maximumMachinesPerStage) - readyMachineItems.Count));
    }

    /// <summary>
    /// The enabled single-product recipe of an item that an available assembler can craft from solids only, or else
    /// that an available furnace smelts from a solid that is not mined (steel from plates), or else that is the fixed
    /// recipe of an available silo (rocket parts). Mined items, ore smelting (left to resource cells on the patch) and
    /// fluid materials are left to other suppliers.
    /// </summary>
    public static (NativeRecipe Recipe, string MachineItem)? Choose(ProductionCatalog catalog, string item, IReadOnlySet<string> machineItems)
    {
        var assemblers = (catalog.Assemblers ?? new Dictionary<string, NativeAssembler>())
            .Where(p => machineItems.Contains(p.Key)).OrderByDescending(p => p.Value.CraftingSpeed).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var furnaces = ResourceCellPlanner.Supply(catalog, item) is null
            ? catalog.Machines.Where(p => machineItems.Contains(p.Key)).OrderByDescending(p => p.Value.CraftingSpeed)
                .ThenBy(p => p.Key, StringComparer.Ordinal).ToArray()
            : [];
        foreach (var recipe in catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == item
                && r.Products[0].DeterministicItem && r.Ingredients.Count > 0 && r.Ingredients.All(i => i.DeterministicItem))
            .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var machine = assemblers.FirstOrDefault(p => p.Value.Accepts(recipe) && p.Value.FixedRecipe is null);
            if (machine.Key is not null) return (recipe, machine.Key);
            var furnace = furnaces.FirstOrDefault(p => FurnaceCellPlanner.Smeltable(recipe, p.Value));
            if (furnace.Key is not null) return (recipe, furnace.Key);
            if (SiloCellPlanner.Machine(catalog, recipe, machineItems) is { } silo) return (recipe, silo);
        }
        return null;
    }

    /// <summary>The cell kind a machine serves: a silo, an assembler, or else a chest-fed burner furnace.</summary>
    private static string Kind(ProductionCatalog catalog, string machineItem) => catalog.Silos?.ContainsKey(machineItem) == true
        ? SiloCellPlanner.Kind : catalog.Assemblers?.ContainsKey(machineItem) == true ? "assembler" : FurnaceCellPlanner.Kind;

    // One silo serves the whole factory, whatever the planned rate.
    private static int Maximum(string kind, int maximumMachinesPerStage) =>
        kind == SiloCellPlanner.Kind ? SiloCellPlanner.MaximumCells : maximumMachinesPerStage;
}
