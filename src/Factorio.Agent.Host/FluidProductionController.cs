using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FluidProductionResult(string Fluid, double TargetStock, double InitialStock, double FinalStock,
    string? MachineId, long StartTick, long EndTick, long CompletedCrafts, int PoweredSamples);

/// <summary>Produces a known-factory fluid stock through native deterministic material processing.</summary>
public sealed class FluidProductionController(IGameClient game, IControllerJournal journal)
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> Dependencies = new();

    public async Task<FluidProductionResult> RunAsync(string fluid, double targetStock, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fluid);
        var ancestors = Dependencies.Value ?? [];
        if (ancestors.Contains(fluid)) throw new InvalidOperationException("Fluid dependencies form a cycle: " + string.Join(" -> ", ancestors.Append(fluid)));
        Dependencies.Value = [.. ancestors, fluid];
        try { return await RunCoreAsync(fluid, targetStock, token); }
        finally { Dependencies.Value = ancestors; }
    }

    private async Task<FluidProductionResult> RunCoreAsync(string fluid, double targetStock, CancellationToken token)
    {
        if (!double.IsFinite(targetStock) || targetStock is <= 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        token = deadline.Token;
        var factoryClient = new FactorySnapshotClient(game);
        FactorySnapshot initial = await factoryClient.CaptureAsync(cancellationToken: token);
        double initialStock = initial.SummarizeStocks().Fluids.GetValueOrDefault(fluid);
        if (initialStock >= targetStock) return new(fluid, targetStock, initialStock, initialStock, null, initial.CollectedTick, initial.CollectedTick, 0, 0);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(catalog.Scope);
        var production = new ProductionController(game, journal);
        var own = await production.ObserveAsync(token);
        RequireScope(own.Scope);
        FluidProductionPlan plan = new FluidProductionPlanner().Choose(fluid, catalog, own.Entities.Select(e => e.AsMachine()).ToArray())
            ?? throw new InvalidOperationException("No enabled deterministic recipe with one fluid product is supported for this stock.");
        var inputs = plan.Recipe.Ingredients.Where(i => i.DeterministicItem).Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (inputs.Length > 8) throw new InvalidOperationException("Fluid recipe exceeds the native capacity probe budget.");
        if (plan.Recipe.Ingredients.Any(i => i.DeterministicFluid && (i.Temperature is not null || i.MinimumTemperature is not null || i.MaximumTemperature is not null)))
            throw new InvalidOperationException("Temperature-constrained processing requires a thermal supply plan.");
        int batchLimit = Math.Min(16, plan.Recipe.Ingredients.Where(i => i.DeterministicItem).GroupBy(i => i.Name)
            .Select(g => checked((int)Math.Floor(catalog.Items[g.Key].StackSize / g.Sum(i => i.Amount!.Value)))).DefaultIfEmpty(16).Min());
        if (batchLimit < 1) throw new InvalidOperationException("An ingredient batch exceeds the supported inventory delivery size.");
        await journal.AppendAsync("fluid-production-start", new { fluid, targetStock, initialStock, plan, initial.Scope, initial.CollectedTick }, token);
        await using var controller = new SpatialController(game, journal);
        var power = new PoweredMachineController(game, journal);
        string machineId = plan.ExistingId ?? await power.InstallAsync(plan.MachineItem, catalog, controller, token);
        var spatial = new SpatialClient(game);
        SpatialSnapshot map = await MapAsync();
        var machine = map.Entities.Single(e => e.Id == machineId);
        await controller.ApproachEntityAsync(machineId, machine.Position, catalog, token);
        if (plan.ExistingId is null || own.Entities.Single(e => e.Id == machineId).Recipe is null)
        {
            if (plan.ExistingId is not null && initial.FluidRecordsAt(machineId).Any(r =>
                r.Data.GetProperty("contents").EnumerateObject().Any(p => p.Value.GetDouble() > 0)))
                throw new InvalidOperationException("Unconfigured machine contains fluid; reconcile it before changing its recipe.");
            var configured = await controller.WorkAsync("set_recipe", new { entityId = machineId, recipe = plan.Recipe.Name }, 600, token: token);
            if (configured.Status != "completed") throw new InvalidOperationException("Native refinery configuration did not complete.");
        }
        map = await MapAsync();
        FactorySnapshot before = await factoryClient.CaptureAsync(cancellationToken: token);
        RequireScope(before.Scope);
        long initialCrafts = Work(before).Data.GetProperty("productsFinished").GetInt64();
        await new FluidBufferController(game, journal).EnsureAsync(machineId, fluid,
            targetStock + plan.Recipe.Products[0].Amount!.Value, catalog, controller, token);
        var prototype = catalog.Assemblers![plan.MachineItem];
        double batches = Math.Ceiling((targetStock - initialStock) / plan.Recipe.Products[0].Amount!.Value);
        double expectedEnergy = batches * plan.Recipe.EnergySeconds * 60 * prototype.EnergyPerTick / prototype.CraftingSpeed;
        await power.MaintainFuelAsync(machineId, expectedEnergy, catalog, controller, true, token);
        int powered = 0;
        for (int attempt = 0; attempt < 1800; attempt++)
        {
            FactorySnapshot current = await factoryClient.CaptureAsync(cancellationToken: token);
            RequireScope(current.Scope);
            FactoryRecord work = Work(current);
            if (!work.Data.TryGetProperty("recipe", out var recipe) || recipe.GetString() != plan.Recipe.Name)
                throw new InvalidDataException("The native fluid-processing recipe changed during execution.");
            long completed = work.Data.GetProperty("productsFinished").GetInt64() - initialCrafts;
            if (completed < 0) throw new InvalidDataException("Native fluid production counter regressed.");
            double amount = current.SummarizeStocks().Fluids.GetValueOrDefault(fluid);
            map = await MapAsync();
            var energy = map.Entities.Single(e => e.Id == machineId).Power;
            if (energy?.Energy > 0 && energy.NetworkId is not null) powered++;
            await journal.AppendAsync("fluid-production-measurement", new
            {
                current.SnapshotId,
                current.CollectedTick,
                machineId,
                completed,
                amount,
                energy,
                work
            }, token);
            if (amount >= targetStock && completed > 0 && powered > 0)
            {
                var result = new FluidProductionResult(fluid, targetStock, initialStock, amount, machineId,
                    initial.CollectedTick, current.CollectedTick, completed, powered);
                await journal.AppendAsync("fluid-production-result", result, token);
                return result;
            }
            if (attempt % 10 == 0)
            {
                var required = Requirements(current);
                await new FluidSupplyController(game, journal).EnsureAsync(machineId, plan.Recipe, required.Fluids, catalog, controller, token);
                await power.MaintainFuelAsync(machineId, expectedEnergy, catalog, controller, false, token);
            }
            foreach (string input in inputs)
            {
                current = await CaptureAsync();
                int needed = Requirements(current).Items[input];
                if (needed <= 0) continue;
                await new ProductionGoalExecutor(game, journal).RunAsync(input, needed, token);
                own = await production.ObserveAsync(token);
                RequireScope(own.Scope);
                await controller.ApproachEntityAsync(machineId, own.Entities.Single(e => e.Id == machineId).Position, catalog, token);
                current = await CaptureAsync();
                own = await production.ObserveAsync(token);
                RequireScope(own.Scope);
                string inventoryId = Work(current).Data.GetProperty("inputInventoryId").GetString()
                    ?? throw new InvalidDataException("Missing processing input identity.");
                var inventory = current.Records.Single(r => r.Id == inventoryId && r.EntityId == machineId && r.Kind == "inventory");
                long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(input).GetProperty("insertable").GetInt64();
                int count = checked((int)Math.Min(Requirements(current).Items[input], Math.Min(capacity, own.Inventory.GetValueOrDefault(input))));
                if (count <= 0) continue;
                var inserted = await controller.WorkAsync("insert", new { entityId = machineId, inventory = "input", item = input, count }, 600, token: token);
                if (inserted.Status != "completed" || inserted.Effects.GetProperty("transferred").GetInt64() != count)
                    throw new InvalidOperationException("Processing input transfer did not complete as requested; reconcile native effects.");
                await journal.AppendAsync("fluid-production-input", new { machineId, input, transferred = count, inserted.OperationId }, token);
            }
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Fluid production wait did not complete; reconcile effects.");
        }
        throw new InvalidOperationException("Fluid production did not establish the requested stock within its observation budget.");

        FactoryRecord Work(FactorySnapshot snapshot) => snapshot.Records.Single(r => r.Kind == "work" && r.EntityId == machineId);
        MachineInputRequirements Requirements(FactorySnapshot snapshot)
        {
            var work = Work(snapshot);
            if (!work.Data.TryGetProperty("recipe", out var recipe) || recipe.GetString() != plan.Recipe.Name)
                throw new InvalidDataException("The native processing recipe changed while preparing inputs.");
            double missing = Math.Max(0, targetStock - snapshot.SummarizeStocks().Fluids.GetValueOrDefault(fluid));
            int needed = checked((int)Math.Ceiling(missing / plan.Recipe.Products[0].Amount!.Value));
            return MachineInputRequirements.From(snapshot, machineId, plan.Recipe, Math.Min(batchLimit, needed));
        }
        async Task<FactorySnapshot> CaptureAsync()
        {
            var value = await factoryClient.CaptureAsync(inputs, cancellationToken: token);
            RequireScope(value.Scope);
            return value;
        }
        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidDataException("Actor scope changed during fluid production; reconcile partial effects.");
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
            RequireScope(value.Scope);
            return value;
        }
    }
}
