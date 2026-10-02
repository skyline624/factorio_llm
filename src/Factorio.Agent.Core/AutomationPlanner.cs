namespace Factorio.Agent.Core;

/// <summary>Kind is the cell kind that serves the stage: assembler, furnace, fluid or silo.</summary>
public sealed record AutomationStage(string Recipe, string Item, string MachineItem, double CraftsPerMinute, int Machines, string Kind = "assembler");
/// <summary>Solid and optional fluid stages for target rates; raw item inputs and external fluid sources are distinct.</summary>
public sealed record AutomationPlan(IReadOnlyList<AutomationStage> Stages, IReadOnlyDictionary<string, double> RawPerMinute)
{
    public IReadOnlyList<FluidSource> FluidSources { get; init; } = [];
}

/// <summary>Sizes factory cells from native recipes, crafting speed and the solid throughput of their basic inserters.</summary>
public static class AutomationPlanner
{
    public const string FluidKind = "fluid";
    /// <summary>Observed native throughput of one basic inserter between a chest and a machine.</summary>
    public const double InserterItemsPerSecond = 0.8;

    public static AutomationPlan Plan(ProductionCatalog catalog, string item, double perMinute, IReadOnlySet<string> machineItems,
        int maximumMachinesPerStage = 8, IReadOnlySet<string>? fluidMachineItems = null) => Plan(catalog,
            new Dictionary<string, double>(StringComparer.Ordinal) { [item] = perMinute }, machineItems, maximumMachinesPerStage, fluidMachineItems);

    /// <summary>Targets share intermediate demand and simultaneous recipe outputs before machines are counted.</summary>
    public static AutomationPlan Plan(ProductionCatalog catalog, IReadOnlyDictionary<string, double> targets, IReadOnlySet<string> machineItems,
        int maximumMachinesPerStage = 8, IReadOnlySet<string>? fluidMachineItems = null)
    {
        if (targets.Count == 0 || targets.Values.Any(rate => !double.IsFinite(rate) || rate <= 0 || rate > 10000))
            throw new ArgumentOutOfRangeException(nameof(targets));
        var graph = ProductionRecipeGraph.Plan(targets, name => Choose(catalog, name, machineItems)
            ?? (fluidMachineItems is null ? null : FluidChainPlanner.Choose(catalog, name, fluidMachineItems)));
        bool Fluid(string name) => fluidMachineItems is not null
            && catalog.Recipes.SelectMany(r => r.Ingredients.Concat(r.Products)).Any(m => m.Name == name && m.DeterministicFluid);
        var raw = graph.Inputs.Where(p => !Fluid(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        IEnumerable<RecipeDemand> ordered = fluidMachineItems is null ? graph.Stages.OrderBy(s => s.Recipe.Name, StringComparer.Ordinal) : graph.Stages;
        var stages = ordered.Select(stage =>
        {
            string kind = stage.Recipe.Ingredients.Concat(stage.Recipe.Products).Any(i => i.DeterministicFluid)
                ? FluidKind : Kind(catalog, stage.MachineItem);
            int machines = (int)Math.Ceiling(stage.CraftsPerMinute / CellCraftsPerMinute(catalog, stage.Recipe, stage.MachineItem) - 1e-9);
            return new AutomationStage(stage.Recipe.Name, stage.Product, stage.MachineItem, stage.CraftsPerMinute,
                Math.Clamp(machines, 1, Maximum(kind, maximumMachinesPerStage)), kind);
        }).ToArray();
        return new(stages, raw)
        {
            FluidSources = graph.Inputs.Where(p => Fluid(p.Key)).Select(p => new FluidSource(p.Key, FluidChainPlanner.Resource(catalog, p.Key), p.Value))
                .OrderBy(s => s.Fluid, StringComparer.Ordinal).ToArray()
        };

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
        double outputs = recipe.Products.Where(p => p.DeterministicItem).Sum(p => p.Amount!.Value);
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
