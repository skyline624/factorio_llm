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
        string input = recipe.Ingredients[0].Name, output = recipe.Products[0].Name;
        long Count(string item) => snapshot.Records.Where(r => r.EntityId == entityId && r.Kind == "inventory")
            .Sum(r => r.Data.GetProperty("items").TryGetProperty(item, out var count) ? count.GetInt64() : 0);
        FactoryRecord? work = snapshot.Records.SingleOrDefault(r => r.EntityId == entityId && r.Kind == "work");
        bool inProcess = work is not null && work.Data.TryGetProperty("recipe", out var currentRecipe)
            && currentRecipe.GetString() == recipe.Name && work.Data.GetProperty("inProcess").GetBoolean();
        long ready = Count(output);
        double outstandingBatches = Math.Max(0, batches - Math.Floor(ready / recipe.Products[0].Amount!.Value) - (inProcess ? 1 : 0));
        int inputToInsert = checked((int)Math.Max(0, outstandingBatches * recipe.Ingredients[0].Amount!.Value - Count(input)));
        return new(inputToInsert, ready, inProcess);
    }
}
