using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record AssemblyResult(string Item, int TargetStock, string? MachineId, long StartTick, long EndTick,
    long FinalStock, long CompletedCrafts, int PoweredSamples);

/// <summary>Produces solid goods in a native assembler; all progress is read from the engine.</summary>
public sealed class AssemblyController(IGameClient game, IControllerJournal journal)
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> Dependencies = new();

    public async Task<AssemblyResult> RunAsync(string item, int targetStock, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        var ancestors = Dependencies.Value ?? [];
        if (ancestors.Contains(item)) throw new InvalidOperationException("Assembly dependencies form a cycle: " + string.Join(" -> ", ancestors.Append(item)));
        Dependencies.Value = [.. ancestors, item];
        try { return await RunCoreAsync(item, targetStock, token); }
        finally { Dependencies.Value = ancestors; }
    }

    private async Task<AssemblyResult> RunCoreAsync(string item, int targetStock, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var production = new ProductionController(game, journal);
        var executor = new ProductionGoalExecutor(game, journal);
        ProductionState initial = await production.ObserveAsync(token);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(catalog.Scope);
        if (initial.Inventory.GetValueOrDefault(item) >= targetStock)
            return new(item, targetStock, null, initial.Tick, initial.Tick, initial.Inventory[item], 0, 0);
        var available = (catalog.Assemblers ?? new Dictionary<string, NativeAssembler>()).Where(p =>
            initial.Inventory.GetValueOrDefault(p.Key) > 0 || initial.Entities.Any(e => e.Name == p.Value.EntityName)
            || catalog.Recipes.Any(r => r.Enabled && catalog.CanHandCraft(r) && r.Products.Any(m => m.Name == p.Key && m.DeterministicItem)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var plan = new AssemblyPlanner().Choose(item, catalog.Recipes, available, initial.Entities.Select(e => e.AsMachine()).ToArray())
            ?? throw new InvalidOperationException("No available assembler for an enabled deterministic solid recipe.");
        NativeRecipe recipe = plan.Recipe;
        string[] inputs = recipe.Ingredients.Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (inputs.Length > 8) throw new InvalidOperationException("Assembly recipe exceeds the native capacity probe budget.");
        // Bound each delivery by one native stack per ingredient, then verify exact insertable counts.
        int batchLimit = Math.Min(16, recipe.Ingredients.GroupBy(i => i.Name)
            .Select(g => checked((int)Math.Floor(catalog.Items[g.Key].StackSize / g.Sum(i => i.Amount!.Value)))).Min());
        if (batchLimit < 1) throw new InvalidOperationException("An ingredient batch exceeds the supported inventory delivery size.");
        await using var controller = new SpatialController(game, journal);
        var power = new PoweredMachineController(game, journal);
        string machineId = plan.ExistingId ?? await power.InstallAsync(plan.MachineItem, catalog, controller, token);
        ProductionEntity installed = (await ObserveAsync()).Entities.Single(e => e.Id == machineId);
        await controller.TravelAsync(installed.Position, 3, catalog, token);
        FactorySnapshot before = await CaptureAsync();
        FactoryRecord beforeWork = Work(before);
        if (!installed.AsMachine().CanProcess(recipe) || (beforeWork.Data.GetProperty("inProcess").GetBoolean()
            && (!beforeWork.Data.TryGetProperty("recipe", out var active) || active.GetString() != recipe.Name)))
            throw new InvalidOperationException("Assembler contents or engaged recipe changed; refuse to replace them.");
        long initialCrafts = beforeWork.Data.GetProperty("productsFinished").GetInt64();
        if (installed.Recipe != recipe.Name)
            await ActAsync("set_recipe", new { entityId = machineId, recipe = recipe.Name });
        await journal.AppendAsync("assembly-start", new { initial.Scope, initial.Tick, item, targetStock, machineId, recipe, initialCrafts }, token);
        int powered = 0;
        for (int attempt = 0; attempt < 1800; attempt++)
        {
            ProductionState state = await ObserveAsync();
            FactorySnapshot snapshot = await CaptureAsync();
            FactoryRecord work = Work(snapshot);
            if (!work.Data.TryGetProperty("recipe", out var configured) || configured.GetString() != recipe.Name)
                throw new InvalidOperationException("The assembler recipe changed; reconcile native effects.");
            long completed = work.Data.GetProperty("productsFinished").GetInt64() - initialCrafts;
            if (completed < 0) throw new InvalidDataException("Native assembler completion counter regressed.");
            long carried = state.Inventory.GetValueOrDefault(item);
            if (carried >= targetStock)
            {
                var result = new AssemblyResult(item, targetStock, machineId, initial.Tick, state.Tick, carried, completed, powered);
                await journal.AppendAsync("assembly-result", result, token);
                return result;
            }
            int batches = Math.Min(batchLimit, checked((int)Math.Ceiling((targetStock - carried) / recipe.Products[0].Amount!.Value)));
            var requirements = AssemblyRequirements.From(snapshot, machineId, recipe, batches);
            await journal.AppendAsync("assembly-measurement", new { snapshot.SnapshotId, snapshot.CollectedTick, machineId, carried, completed, requirements }, token);
            if (requirements.ReadyOutput > 0)
            {
                await controller.TravelAsync(installed.Position, 3, catalog, token);
                await ActAsync("take", new { entityId = machineId, inventory = "output", item, count = Math.Min(requirements.ReadyOutput, targetStock - carried) });
                continue;
            }
            if (attempt % 10 == 0)
            {
                double energy = batches * recipe.EnergySeconds * 60 * available[plan.MachineItem].EnergyPerTick / available[plan.MachineItem].CraftingSpeed;
                await power.MaintainFuelAsync(machineId, energy, catalog, controller, attempt == 0, token);
                await controller.TravelAsync(installed.Position, 3, catalog, token);
                var map = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
                RequireScope(map.Scope);
                if (map.Entities.Single(e => e.Id == machineId).Power is { NetworkId: not null, Energy: > 0 }) powered++;
            }
            foreach (string input in inputs)
            {
                snapshot = await CaptureAsync();
                requirements = AssemblyRequirements.From(snapshot, machineId, recipe, batches);
                int needed = requirements.InputsToInsert[input];
                if (needed <= 0) continue;
                await executor.RunAsync(input, needed, token);
                await controller.TravelAsync(installed.Position, 3, catalog, token);
                state = await ObserveAsync();
                snapshot = await CaptureAsync();
                requirements = AssemblyRequirements.From(snapshot, machineId, recipe, batches);
                string inventoryId = Work(snapshot).Data.GetProperty("inputInventoryId").GetString()
                    ?? throw new InvalidDataException("Missing assembler input identity.");
                var inventory = snapshot.Records.Single(r => r.Id == inventoryId && r.EntityId == machineId && r.Kind == "inventory");
                long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(input).GetProperty("insertable").GetInt64();
                int count = checked((int)Math.Min(requirements.InputsToInsert[input], Math.Min(capacity, state.Inventory.GetValueOrDefault(input))));
                if (count > 0) await ActAsync("insert", new { entityId = machineId, inventory = "input", item = input, count });
            }
            await ActAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Assembly exhausted its native observation budget.");

        FactoryRecord Work(FactorySnapshot snapshot) => snapshot.Records.Single(r => r.EntityId == machineId && r.Kind == "work");
        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await new FactorySnapshotClient(game).CaptureAsync(inputs, cancellationToken: token);
            RequireScope(value.Scope);
            return value;
        }
        async Task<ProductionState> ObserveAsync()
        {
            var value = await production.ObserveAsync(token);
            RequireScope(value.Scope);
            if (value.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
            return value;
        }
        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidDataException("Actor identity changed during assembly; reconcile partial effects.");
        }
        async Task ActAsync(string kind, object args)
        {
            var receipt = await controller.WorkAsync(kind, args, 36000, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"Assembly action {kind} ended with {receipt.Status}: {receipt.Error?.Code}. Reconcile partial effects.");
        }
    }
}
