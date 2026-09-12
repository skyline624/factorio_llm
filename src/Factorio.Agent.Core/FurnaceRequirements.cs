namespace Factorio.Agent.Core;

/// <summary>One coherent native photograph accounts for input, engaged ingredients and completed output.</summary>
public sealed record FurnaceRequirements(int InputToInsert, long ReadyOutput, bool InProcess)
{
    public static FurnaceRequirements From(FactorySnapshot snapshot, string entityId, NativeRecipe recipe, int batches)
    {
        if (batches < 1 || recipe.Ingredients.Count != 1 || recipe.Products.Count != 1
            || !recipe.Ingredients[0].DeterministicItem || !recipe.Products[0].DeterministicItem
            || recipe.Ingredients[0].Name == recipe.Products[0].Name)
            throw new InvalidOperationException("Furnace accounting requires distinct deterministic solid input and output.");
        AssemblyRequirements requirements = AssemblyRequirements.From(snapshot, entityId, recipe, batches);
        return new(requirements.InputsToInsert[recipe.Ingredients[0].Name], requirements.ReadyOutput, requirements.InProcess);
    }
}
