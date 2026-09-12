using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FluidProductionResult(string Fluid, double TargetStock, double InitialStock, double FinalStock,
    string? MachineId, long StartTick, long EndTick, long CompletedCrafts, int PoweredSamples);

/// <summary>Produces a known-factory fluid stock through a native one-input, one-output conversion.</summary>
public sealed class FluidProductionController(IGameClient game, IControllerJournal journal)
{
    public async Task<FluidProductionResult> RunAsync(string fluid, double targetStock, CancellationToken token = default)
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
        var own = await new ProductionController(game, journal).ObserveAsync(token);
        RequireScope(own.Scope);
        FluidProductionPlan plan = new FluidProductionPlanner().Choose(fluid, catalog, own.Entities.Select(e => e.AsMachine()).ToArray())
            ?? throw new InvalidOperationException("No enabled deterministic one-input, one-output fluid conversion is supported for this stock.");
        string input = plan.Recipe.Ingredients[0].Name;
        var sourceCandidates = own.Entities.Where(e => e.Id != plan.ExistingId && initial.FluidStockAt(e.Id, input) > 0).ToArray();
        if (sourceCandidates.Length == 0) throw new InvalidOperationException("No known source contains the required input fluid; prepare extraction first.");
        await journal.AppendAsync("fluid-production-start", new { fluid, targetStock, initialStock, plan, initial.Scope, initial.CollectedTick }, token);
        await using var controller = new SpatialController(game, journal);
        var power = new PoweredMachineController(game, journal);
        string machineId = plan.ExistingId ?? await power.InstallAsync(plan.MachineItem, catalog, controller, token);
        var spatial = new SpatialClient(game);
        SpatialSnapshot map = await MapAsync();
        var machine = map.Entities.Single(e => e.Id == machineId);
        MapPosition approach = new PlacementPlanner().FindInteractionApproach(new(map), machine)
            ?? throw new InvalidOperationException("No reachable interaction position for the fluid-processing machine.");
        await controller.NavigateAsync(approach, .2, token);
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
        var source = map.Entities.Where(e => sourceCandidates.Any(s => s.Id == e.Id)
            && e.FluidConnections?.Any(c => c.Type == "normal" && c.FlowDirection is "output" or "input-output"
                && (c.Filter is null || c.Filter == input)) == true)
            .OrderBy(e => e.Position.DistanceTo(machine.Position)).FirstOrDefault()
            ?? throw new InvalidOperationException("The fluid source has no compatible output port in the current construction area.");
        await new PipeConnectionController(game, journal).RunAsync(source.Id, machineId, input, token);
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
            if (attempt % 10 == 0) await power.MaintainFuelAsync(machineId, expectedEnergy, catalog, controller, false, token);
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Fluid production wait did not complete; reconcile effects.");
        }
        throw new InvalidOperationException("Fluid production did not establish the requested stock within its observation budget.");

        FactoryRecord Work(FactorySnapshot snapshot) => snapshot.Records.Single(r => r.Kind == "work" && r.EntityId == machineId);
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
