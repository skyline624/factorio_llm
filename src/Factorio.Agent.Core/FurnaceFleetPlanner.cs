namespace Factorio.Agent.Core;

public sealed record FurnaceFleetMachine(string EntityId, long Input, long Output, bool InProcess, long InputCapacity, double CraftingSpeed);
public sealed record FurnaceFleetLoad(string EntityId, int InputToInsert, int TotalCycles);
public sealed record FurnaceFleetPlan(long ReadyOutput, long CommittedOutput, IReadOnlyList<FurnaceFleetLoad> Loads);

/// <summary>Balances native furnace work without counting carried, queued or engaged products twice.</summary>
public static class FurnaceFleetPlanner
{
    public const int MaximumMachines = 8;

    public static int RequiredMachines(NativeRecipe recipe, int batches, double craftingSpeed)
    {
        ValidateRecipe(recipe);
        if (batches < 1 || !double.IsFinite(craftingSpeed) || craftingSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(batches));
        // Ten game minutes is the expansion target, not an execution deadline or completion claim.
        return checked((int)Math.Clamp(Math.Ceiling(batches * recipe.EnergySeconds / craftingSpeed / 600), 1, MaximumMachines));
    }

    public static FurnaceFleetPlan Plan(NativeRecipe recipe, int targetStock, long carried, IReadOnlyList<FurnaceFleetMachine> machines)
    {
        ValidateRecipe(recipe);
        if (targetStock is < 1 or > 1000 || carried < 0 || machines.Count is < 1 or > MaximumMachines
            || machines.Select(m => m.EntityId).Distinct(StringComparer.Ordinal).Count() != machines.Count
            || machines.Any(m => string.IsNullOrWhiteSpace(m.EntityId) || m.Input < 0 || m.Output < 0 || m.InputCapacity < 0
                || !double.IsFinite(m.CraftingSpeed) || m.CraftingSpeed <= 0))
            throw new InvalidDataException("Invalid native furnace fleet stock, identity, capacity or speed.");
        long inputUnits = checked((long)recipe.Ingredients[0].Amount!.Value), yield = checked((long)recipe.Products[0].Amount!.Value);
        long ready = machines.Sum(m => m.Output);
        int[] queued = machines.Select(m => checked((int)(m.Input / inputUnits + (m.InProcess ? 1 : 0)))).ToArray();
        long committed = checked(queued.Sum(q => (long)q) * yield);
        long accounted = checked(carried + ready + committed);
        int remaining = checked((int)Math.Ceiling(Math.Max(0, targetStock - accounted) / (double)yield));
        int[] assigned = (int[])queued.Clone(), inputs = new int[machines.Count];
        int budget = 1000;
        for (int cycle = 0; cycle < remaining; cycle++)
        {
            var next = Enumerable.Range(0, machines.Count).Select(index =>
            {
                var m = machines[index];
                long additional = Math.Max(0, checked((assigned[index] + 1L - (m.InProcess ? 1 : 0)) * inputUnits - m.Input));
                return new { Index = index, Additional = additional, Cost = additional - inputs[index],
                    Finish = (assigned[index] + 1) * recipe.EnergySeconds / m.CraftingSpeed };
            }).Where(p => p.Additional <= machines[p.Index].InputCapacity && p.Cost <= budget)
                .OrderBy(p => p.Finish).ThenBy(p => machines[p.Index].EntityId, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) break;
            budget -= checked((int)next.Cost);
            inputs[next.Index] = checked((int)next.Additional);
            assigned[next.Index]++;
        }
        return new(ready, committed, machines.Select((m, index) => new FurnaceFleetLoad(m.EntityId, inputs[index], assigned[index])).ToArray());
    }

    public static FurnaceFleetMachine Read(FactorySnapshot snapshot, string entityId, NativeRecipe recipe)
    {
        ValidateRecipe(recipe);
        var required = MachineInputRequirements.From(snapshot, entityId, recipe, 0);
        var work = snapshot.Records.Single(r => r.Kind == "work" && r.EntityId == entityId);
        FactoryRecord Inventory(string key) => snapshot.Records.Single(r => r.Kind == "inventory" && r.EntityId == entityId
            && r.Id == work.Data.GetProperty(key).GetString());
        var input = Inventory("inputInventoryId").Data;
        var output = Inventory("outputInventoryId").Data;
        string ingredient = recipe.Ingredients[0].Name, product = recipe.Products[0].Name;
        long carriedInput = input.GetProperty("items").TryGetProperty(ingredient, out var amount) ? amount.GetInt64() : 0;
        long ready = output.GetProperty("items").TryGetProperty(product, out var count) ? count.GetInt64() : 0;
        double speed = work.Data.GetProperty("craftingSpeed").GetDouble();
        if (!double.IsFinite(speed) || speed <= 0) throw new InvalidDataException("Missing or invalid native current furnace speed.");
        return new(entityId, carriedInput, ready, required.InProcess,
            input.GetProperty("capacityHints").GetProperty(ingredient).GetProperty("insertable").GetInt64(),
            speed);
    }

    private static void ValidateRecipe(NativeRecipe recipe)
    {
        if (recipe.Ingredients.Count != 1 || recipe.Products.Count != 1 || !recipe.Ingredients[0].DeterministicItem
            || !recipe.Products[0].DeterministicItem || recipe.Ingredients[0].Name == recipe.Products[0].Name
            || !double.IsFinite(recipe.EnergySeconds) || recipe.EnergySeconds <= 0)
            throw new InvalidDataException("Furnace fleets require one distinct deterministic solid input and output.");
    }
}
