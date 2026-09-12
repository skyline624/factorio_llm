using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepares finite native fluid stocks and connects them to configured material-processing inputs.</summary>
public sealed class FluidSupplyController(IGameClient game, IControllerJournal journal)
{
    public async Task<string> EnsureAsync(string machineId, NativeRecipe recipe, IReadOnlyDictionary<string, double> needed,
        ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var factory = new FactorySnapshotClient(game);
        var spatial = new SpatialClient(game);
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var fluids = new List<string>();
        foreach (var input in recipe.Ingredients.Where(i => i.DeterministicFluid).GroupBy(i => i.Name))
        {
            if (input.Any(i => i.Temperature is not null || i.MinimumTemperature is not null || i.MaximumTemperature is not null))
                throw new InvalidOperationException("Temperature-constrained chemical inputs require a thermal supply plan.");
            double required = needed[input.Key];
            if (required <= 0) continue;
            fluids.Add(input.Key);
            // Base-game water is extracted from observed terrain, rather than converted by a recipe.
            bool terrainFluid = input.Key == "water";
            FactorySnapshot stock = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(stock.Scope);
            double total = stock.SummarizeStocks().Fluids.GetValueOrDefault(input.Key);
            double inMachine = stock.FluidStockAt(machineId, input.Key);
            if (!terrainFluid && total - inMachine < required)
                await new FluidProductionController(game, journal).RunAsync(input.Key, required + inMachine, token);
        }
        if (fluids.Count == 0) return machineId;
        var owned = await new ProductionController(game, journal).ObserveAsync(token);
        RequireScope(owned.Scope);
        await controller.ApproachEntityAsync(machineId, owned.Entities.Single(e => e.Id == machineId).Position, catalog, token);
        var placement = await new ChemicalPlacementController(game, journal).EnsureAsync(machineId, recipe, catalog, controller, token);
        if (placement.MachineId != machineId)
        {
            machineId = placement.MachineId;
            fluids = recipe.Ingredients.Where(i => i.DeterministicFluid).Select(i => i.Name).Distinct(StringComparer.Ordinal).ToList();
            needed = needed.ToDictionary(p => p.Key, p => p.Value + placement.DiscardedInputBuffers.GetValueOrDefault(p.Key), StringComparer.Ordinal);
            foreach (string fluid in fluids.Where(f => f != "water"))
            {
                var remainingStock = await factory.CaptureAsync(cancellationToken: token);
                RequireScope(remainingStock.Scope);
                double inMachine = remainingStock.FluidStockAt(machineId, fluid);
                if (remainingStock.SummarizeStocks().Fluids.GetValueOrDefault(fluid) - inMachine < needed[fluid])
                    await new FluidProductionController(game, journal).RunAsync(fluid, needed[fluid] + inMachine, token);
            }
        }
        var map = await spatial.CaptureAsync([pipeItem], 48, token);
        var available = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(map.Scope);
        RequireScope(available.Scope);
        if (fluids.Contains("water") && new FluidSupplyPlanner().Find(map, available, pipeItem, machineId, "water", token) is null)
        {
            if (fluids.Count > 1) throw new InvalidOperationException("Prepare an independent water supply before planning multiple fluid routes.");
            await new OffshoreSupplyController(game, journal).ConnectAsync(machineId, "water", catalog, controller, token);
            return machineId;
        }
        var routes = new MultiFluidSupplyPlanner().Find(map, available, pipeItem, machineId, fluids, token)
            ?? throw new InvalidOperationException("The observed machine has no compatible joint routing for all required fluids.");
        await journal.AppendAsync("chemical-fluid-routing", new { machineId, routes, available.SnapshotId, available.CollectedTick }, token);
        foreach (var route in routes)
        {
            await journal.AppendAsync("chemical-fluid-supply", new { machineId, fluid = route.Fluid, required = needed[route.Fluid],
                route = route.Supply, available.SnapshotId, available.CollectedTick }, token);
            await new PipeConnectionController(game, journal).RunAsync(route.Supply.SourceId, machineId, route.Fluid, token);
        }
        return machineId;
        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during chemical fluid supply.");
        }
    }
}
