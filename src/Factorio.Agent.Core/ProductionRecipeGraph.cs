namespace Factorio.Agent.Core;

internal sealed record RecipeDemand(NativeRecipe Recipe, string MachineItem, string Product, double CraftsPerMinute);
internal sealed record RecipeGraph(IReadOnlyList<RecipeDemand> Stages, IReadOnlyDictionary<string, double> Inputs);

/// <summary>Shares recipe cycles across targets, including the simultaneous outputs of a fluid recipe.</summary>
internal static class ProductionRecipeGraph
{
    public static RecipeGraph Plan(IReadOnlyDictionary<string, double> targets,
        Func<string, (NativeRecipe Recipe, string MachineItem)?> choose)
    {
        var choices = new Dictionary<string, (NativeRecipe Recipe, string MachineItem)?>(StringComparer.Ordinal);
        var simultaneous = new Dictionary<string, (NativeRecipe Recipe, string MachineItem)>(StringComparer.Ordinal);
        foreach (string target in targets.Keys.Order(StringComparer.Ordinal)) Discover(target, []);
        // A refinery already required for heavy or light oil also supplies its gas: no second basic refinery for that gas.
        var producers = simultaneous.Values.OrderBy(p => p.Recipe.Name, StringComparer.Ordinal).ToArray();
        foreach (var producer in producers)
            foreach (var output in producer.Recipe.Products)
                choices[output.Name] = producer;

        var stages = new Dictionary<string, RecipeDemand>(StringComparer.Ordinal);
        var demand = new Dictionary<(string Recipe, string Product), double>();
        var inputs = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (target, rate) in targets.OrderBy(p => p.Key, StringComparer.Ordinal)) Add(target, rate, []);
        return new(order.Select(name => stages[name]).ToArray(), inputs);

        (NativeRecipe Recipe, string MachineItem)? Choice(string product)
        {
            if (!choices.TryGetValue(product, out var result)) choices[product] = result = choose(product);
            return result;
        }

        void Discover(string product, IReadOnlyList<string> path)
        {
            Guard(product, path);
            if (choices.ContainsKey(product)) return;
            if (Choice(product) is not { } selected) return;
            if (selected.Recipe.Products.Count > 1) simultaneous[selected.Recipe.Name] = selected;
            foreach (var input in selected.Recipe.Ingredients) Discover(input.Name, [.. path, product]);
        }

        void Add(string product, double rate, IReadOnlyList<string> path)
        {
            Guard(product, path);
            if (Choice(product) is not { } selected)
            {
                inputs[product] = inputs.GetValueOrDefault(product) + rate;
                return;
            }
            var (recipe, machine) = selected;
            var output = recipe.Products.Single(p => p.Name == product);
            var key = (recipe.Name, product);
            demand[key] = demand.GetValueOrDefault(key) + rate;
            double needed = recipe.Products.Max(p => demand.GetValueOrDefault((recipe.Name, p.Name)) / p.Amount!.Value);
            double delta = needed - (stages.TryGetValue(recipe.Name, out var old) ? old.CraftsPerMinute : 0);
            if (delta <= 1e-9) return;
            // Only the additional cycles consume ingredients. Other outputs are credits from those same cycles.
            stages[recipe.Name] = new(recipe, machine, old?.Product ?? output.Name, needed);
            foreach (var ingredient in recipe.Ingredients) Add(ingredient.Name, delta * ingredient.Amount!.Value, [.. path, product]);
            if (!order.Contains(recipe.Name, StringComparer.Ordinal)) order.Add(recipe.Name);
        }
    }

    private static void Guard(string product, IReadOnlyList<string> path)
    {
        if (path.Contains(product)) throw new InvalidOperationException("Automation recipes form a cycle: " + string.Join(" -> ", path.Append(product)));
        if (path.Count > 32) throw new InvalidOperationException("Automation dependency depth exceeds its budget.");
    }
}
