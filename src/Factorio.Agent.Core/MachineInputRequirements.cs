namespace Factorio.Agent.Core;

public sealed record MachineInputRequirements(IReadOnlyDictionary<string, int> Items, IReadOnlyDictionary<string, double> Fluids, bool InProcess)
{
    public static MachineInputRequirements From(FactorySnapshot snapshot, string entityId, NativeRecipe recipe, int batches)
    {
        if (batches < 0 || recipe.Products.Count != 1 || !(recipe.Products[0].DeterministicItem || recipe.Products[0].DeterministicFluid)
            || recipe.Ingredients.Any(i => !(i.DeterministicItem || i.DeterministicFluid) || i.Name == recipe.Products[0].Name))
            throw new InvalidOperationException("Input accounting requires deterministic materials and one distinct product.");
        snapshot.SummarizeStocks();
        var work = snapshot.Records.Single(r => r.EntityId == entityId && r.Kind == "work");
        bool inProcess = work.Data.GetProperty("inProcess").GetBoolean();
        if (inProcess && (!work.Data.TryGetProperty("recipe", out var current) || current.GetString() != recipe.Name))
            throw new InvalidDataException("The machine has an engaged cycle for another recipe.");
        int outstanding = Math.Max(0, batches - (inProcess ? 1 : 0));
        long Count(string item)
        {
            string inventoryId = work.Data.GetProperty("inputInventoryId").GetString()
                ?? throw new InvalidDataException("Missing native input inventory identity.");
            var inventory = snapshot.Records.Single(r => r.Id == inventoryId && r.EntityId == entityId && r.Kind == "inventory");
            return inventory.Data.GetProperty("items").TryGetProperty(item, out var count) ? count.GetInt64() : 0;
        }
        var items = recipe.Ingredients.Where(i => i.DeterministicItem).GroupBy(i => i.Name).ToDictionary(g => g.Key,
            g => checked((int)Math.Max(0, outstanding * g.Sum(i => i.Amount!.Value) - Count(g.Key))), StringComparer.Ordinal);
        var fluids = recipe.Ingredients.Where(i => i.DeterministicFluid).GroupBy(i => i.Name).ToDictionary(g => g.Key,
            g => Math.Max(0, outstanding * g.Sum(i => i.Amount!.Value) - snapshot.FluidStockAt(entityId, g.Key)), StringComparer.Ordinal);
        return new(items, fluids, inProcess);
    }
}
