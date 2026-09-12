namespace Factorio.Agent.Core;

/// <summary>Limits automatic relocation to unused machines without items or an engaged craft.</summary>
public static class ChemicalRelocationGuard
{
    public static IReadOnlyDictionary<string, double> Inspect(FactorySnapshot snapshot, string entityId, NativeRecipe recipe)
    {
        snapshot.SummarizeStocks();
        var work = snapshot.Records.Single(r => r.EntityId == entityId && r.Kind == "work");
        if (work.Data.GetProperty("recipe").GetString() != recipe.Name || work.Data.GetProperty("inProcess").GetBoolean()
            || work.Data.GetProperty("productsFinished").GetInt64() != 0)
            throw new InvalidOperationException("Only an unused, idle configured machine can be relocated automatically.");
        var entity = snapshot.Records.Single(r => r.EntityId == entityId && r.Kind == "entity");
        foreach (var id in entity.Data.GetProperty("inventories").EnumerateArray())
        {
            var inventory = snapshot.Records.Single(r => r.Id == id.GetString() && r.EntityId == entityId && r.Kind == "inventory");
            if (inventory.Data.GetProperty("items").EnumerateObject().Any(p => p.Value.GetInt64() > 0))
                throw new InvalidOperationException("Relocation requires every machine inventory to be empty.");
        }
        var allowed = recipe.Ingredients.Where(i => i.DeterministicFluid).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var discarded = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var record in snapshot.FluidRecordsAt(entityId))
        {
            if (record.Data.GetProperty("sourceBoxes").EnumerateArray().Any(b => b.GetProperty("entityId").GetString() != entityId))
                throw new InvalidOperationException("Shared fluid stores cannot be treated as disposable machine buffers.");
            foreach (var fluid in record.Data.GetProperty("contents").EnumerateObject().Where(p => p.Value.GetDouble() > 0))
            {
                if (!allowed.Contains(fluid.Name)) throw new InvalidOperationException("Relocation would discard a non-input fluid.");
                discarded[fluid.Name] = discarded.GetValueOrDefault(fluid.Name) + fluid.Value.GetDouble();
            }
        }
        return discarded;
    }
}
